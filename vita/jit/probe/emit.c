/* Shepherd JIT probe: the small hand-encoded routines the probe places in code memory to time and verify the platform's
 * code path (vita/jit/probe/README.md). A32 and T32 (Thumb-2) for the Cortex-A9 (ARMv7-A), A64 for the Mac harness. Each is a
 * complete leaf function in the platform calling convention: int f (int) -> r0 / w0. This file only fills a byte buffer;
 * probe_core.c decides whether and when the platform makes a buffer runnable.
 * T32 is the ISA kit #8's copy-and-patch stencils use (vita/notes/kit8-baseline-jit-design.md section 1), so every routine has a T32
 * form: MOVW/MOVT immediate pairs, BLX through a register, and the +-16 MiB Thumb-2 BL (BL to a Thumb target, BLX imm to an ARM one).
 * The A32 / T32 encodings are checked by execution under qemu (vita/jit/probe/qemu/run.sh) before any device run. */
#include <string.h>
#include "jitp.h"

static void w32 (uint8_t *p, uint32_t v) { memcpy (p, &v, 4); }
static void w16 (uint8_t *p, uint16_t v) { memcpy (p, &v, 2); }

static uint32_t a32_movw (int rd, uint32_t imm) { return 0xE3000000u | ((imm >> 12) & 0xF) << 16 | (uint32_t) rd << 12 | (imm & 0xFFF); }
static uint32_t a32_movt (int rd, uint32_t imm) { return 0xE3400000u | ((imm >> 12) & 0xF) << 16 | (uint32_t) rd << 12 | (imm & 0xFFF); }
#define A32_BX_LR      0xE12FFF1Eu
#define A32_BLX_R3     0xE12FFF33u
#define A32_PUSH_R4_LR 0xE92D4010u
#define A32_POP_R4_PC  0xE8BD8010u
#define A32_SUBS_R0_1  0xE2500001u
#define A32_BNE_M12    0x1AFFFFFDu
#define A32_MOV_R0_0   0xE3A00000u

/* Thumb-2 MOVW (T3) / MOVT (T1): hw1 = 11110 i 10 x 1 0 0 imm4 (x = 0 MOVW, 1 MOVT), hw2 = 0 imm3 Rd imm8 */
static void t32_mov16 (uint8_t *p, int top, int rd, uint32_t imm)
{
	uint16_t hw1 = (uint16_t) ((top ? 0xF2C0u : 0xF240u) | ((imm >> 11) & 1) << 10 | ((imm >> 12) & 0xF));
	uint16_t hw2 = (uint16_t) (((imm >> 8) & 7) << 12 | (uint32_t) rd << 8 | (imm & 0xFF));
	w16 (p, hw1); w16 (p + 2, hw2);
}
#define T16_BX_LR      0x4770u
#define T16_NOP        0xBF00u
#define T16_PUSH_R4_LR 0xB510u
#define T16_POP_R4_PC  0xBD10u
#define T16_BLX_R3     0x4798u
#define T16_SUBS_R0_1  0x3801u   /* SUBS r0, #1 (T2) */
#define T16_BNE_M4     0xD1FDu   /* BNE back to the SUBS two bytes before it (target = PC + 4 - 6) */
#define T16_MOVS_R0_0  0x2000u

static uint32_t a64_movz_w (int rd, uint32_t imm) { return 0x52800000u | (imm & 0xFFFF) << 5 | (uint32_t) rd; }
#define A64_RET 0xD65F03C0u

int emit_ret_imm (int isa, uint8_t *p, uint32_t v)
{
	switch (isa) {
	case ISA_A32: w32 (p, a32_movw (0, v)); w32 (p + 4, A32_BX_LR); return 8;
	case ISA_T32: t32_mov16 (p, 0, 0, v); w16 (p + 4, T16_BX_LR); w16 (p + 6, T16_NOP); return 8;
	case ISA_A64: w32 (p, a64_movz_w (0, v)); w32 (p + 4, A64_RET); return 8;
	}
	return 0;
}

uint32_t emit_patch_word (int isa, uint32_t v, int *offset)
{
	uint8_t b [8] = {0}; *offset = 0; emit_ret_imm (isa, b, v);
	uint32_t w; memcpy (&w, b, 4); return w;
}

int emit_ret_arg_plus (int isa, uint8_t *p, uint32_t k)
{
	switch (isa) {
	case ISA_A32: w32 (p, 0xE2800000u | (k & 0xFF)); w32 (p + 4, A32_BX_LR); return 8;
	case ISA_A64: w32 (p, 0x11000000u | (k & 0xFFF) << 10); w32 (p + 4, A64_RET); return 8;
	case ISA_T32: w16 (p, (uint16_t) (0x3000u | (k & 0xFF))); w16 (p + 2, T16_BX_LR); return 4;
	}
	return 0;
}

/* a register-resident countdown: SUBS r0,#1 / BNE back / return 0 -- the same two loop instructions as the eboot-side baseline
 * (probe_core.c jitp_native_countdown_*), so the exec-ratio test compares like with like */
int emit_countdown (int isa, uint8_t *p)
{
	switch (isa) {
	case ISA_A32: w32 (p, A32_SUBS_R0_1); w32 (p + 4, A32_BNE_M12); w32 (p + 8, A32_MOV_R0_0); w32 (p + 12, A32_BX_LR); return 16;
	case ISA_T32: w16 (p, T16_SUBS_R0_1); w16 (p + 2, T16_BNE_M4); w16 (p + 4, T16_MOVS_R0_0); w16 (p + 6, T16_BX_LR); return 8;
	case ISA_A64: w32 (p, 0x71000400u); w32 (p + 4, 0x54FFFFE1u); w32 (p + 8, a64_movz_w (0, 0)); w32 (p + 12, A64_RET); return 16;
	}
	return 0;
}

/* call out through a register: always reachable (MOVW/MOVT the helper's address, BLX) */
int emit_call_helper (int isa, uint8_t *p, void *helper, uint32_t arg)
{
	uintptr_t h = (uintptr_t) helper;
	switch (isa) {
	case ISA_A32:
		w32 (p, A32_PUSH_R4_LR); w32 (p + 4, a32_movw (0, arg & 0xFFFF));
		w32 (p + 8, a32_movw (3, (uint32_t) h & 0xFFFF)); w32 (p + 12, a32_movt (3, (uint32_t) h >> 16));
		w32 (p + 16, A32_BLX_R3); w32 (p + 20, A32_POP_R4_PC); return 24;
	case ISA_T32:
		w16 (p, T16_PUSH_R4_LR); t32_mov16 (p + 2, 0, 0, arg & 0xFFFF);
		t32_mov16 (p + 6, 0, 3, (uint32_t) h & 0xFFFF); t32_mov16 (p + 10, 1, 3, (uint32_t) h >> 16);   /* bit 0 kept: BLX interworks */
		w16 (p + 14, T16_BLX_R3); w16 (p + 16, T16_POP_R4_PC); w16 (p + 18, T16_NOP); return 20;
	case ISA_A64: {
		uint64_t a = (uint64_t) h; int n = 0;
		w32 (p + n, 0xA9BF7BFDu); n += 4; w32 (p + n, 0x910003FDu); n += 4;
		w32 (p + n, 0xD2800010u | (uint32_t) (a & 0xFFFF) << 5); n += 4;
		w32 (p + n, 0xF2A00010u | (uint32_t) ((a >> 16) & 0xFFFF) << 5); n += 4;
		w32 (p + n, 0xF2C00010u | (uint32_t) ((a >> 32) & 0xFFFF) << 5); n += 4;
		w32 (p + n, 0xF2E00010u | (uint32_t) ((a >> 48) & 0xFFFF) << 5); n += 4;
		w32 (p + n, a64_movz_w (0, arg & 0xFFFF)); n += 4; w32 (p + n, 0xD63F0200u); n += 4;
		w32 (p + n, 0xA8C17BFDu); n += 4; w32 (p + n, A64_RET); n += 4; return n; }
	}
	return 0;
}

/* the reach of a direct call from p (the BL's own position inside emit_bl_helper's routine) to helper, in bytes; 0 = out of reach */
int emit_bl_reach (int isa, uint8_t *p, void *helper, int64_t *dist)
{
	uintptr_t h = (uintptr_t) helper;
	switch (isa) {
	case ISA_A32: { int64_t off = (int64_t) (h & ~(uintptr_t) 1) - (int64_t) ((uintptr_t) p + 8 + 8); *dist = off;
		return off >= -(32ll << 20) && off < (32ll << 20); }
	case ISA_T32: { uintptr_t blpc = (uintptr_t) p + 6, pc = blpc + 4;
		int64_t off = (h & 1) ? (int64_t) (h & ~(uintptr_t) 1) - (int64_t) pc : (int64_t) h - (int64_t) (pc & ~(uintptr_t) 3); *dist = off;
		return off >= -(16ll << 20) && off < (16ll << 20); }
	case ISA_A64: { int64_t off = (int64_t) h - (int64_t) ((uintptr_t) p + 12); *dist = off;
		return off >= -(128ll << 20) && off < (128ll << 20); }
	}
	*dist = 0; return 0;
}

/* call out with a direct, displacement-patched BL (the stencil patcher's call hole); returns 0 (nothing written) when out of reach */
int emit_bl_helper (int isa, uint8_t *p, void *helper, uint32_t arg, int64_t *dist)
{
	uintptr_t h = (uintptr_t) helper;
	if (!emit_bl_reach (isa, p, helper, dist)) return 0;
	int64_t off = *dist;
	switch (isa) {
	case ISA_A32:
		w32 (p, A32_PUSH_R4_LR); w32 (p + 4, a32_movw (0, arg & 0xFFFF));
		if (h & 1) w32 (p + 8, 0xFA000000u | ((uint32_t) (off >> 1) & 1) << 24 | ((uint32_t) (off >> 2) & 0xFFFFFF));   /* BLX imm -> Thumb */
		else w32 (p + 8, 0xEB000000u | ((uint32_t) (off >> 2) & 0xFFFFFF));
		w32 (p + 12, A32_POP_R4_PC); return 16;
	case ISA_T32: {
		/* T1 BL (Thumb target) / T2 BLX imm (ARM target): hw1 = 11110 S imm10, hw2 = 11 J1 x J2 imm11 (x = 1 BL, 0 BLX; BLX's
		 * imm11 = imm10L:H with H = 0); I1 = NOT(J1 ^ S), I2 = NOT(J2 ^ S); offset = S:I1:I2:imm10:imm11:0 */
		uint32_t o = (uint32_t) off, s = (o >> 24) & 1, i1 = (o >> 23) & 1, i2 = (o >> 22) & 1;
		uint32_t j1 = (~i1 ^ s) & 1, j2 = (~i2 ^ s) & 1;
		uint16_t hw1 = (uint16_t) (0xF000u | s << 10 | ((o >> 12) & 0x3FF));
		uint16_t hw2 = (h & 1) ? (uint16_t) (0xD000u | j1 << 13 | j2 << 11 | ((o >> 1) & 0x7FF))
		                       : (uint16_t) (0xC000u | j1 << 13 | j2 << 11 | ((o >> 1) & 0x7FE));
		w16 (p, T16_PUSH_R4_LR); t32_mov16 (p + 2, 0, 0, arg & 0xFFFF);
		w16 (p + 6, hw1); w16 (p + 8, hw2); w16 (p + 10, T16_POP_R4_PC); return 12; }
	case ISA_A64:
		w32 (p, 0xA9BF7BFDu); w32 (p + 4, 0x910003FDu); w32 (p + 8, a64_movz_w (0, arg & 0xFFFF));
		w32 (p + 12, 0x94000000u | ((uint32_t) (off >> 2) & 0x3FFFFFF)); w32 (p + 16, 0xA8C17BFDu); w32 (p + 20, A64_RET); return 24;
	}
	return 0;
}

void *emit_entry (int isa, void *p) { return isa == ISA_T32 ? (void *) ((uintptr_t) p | 1) : p; }

const char *isa_name (int isa) { return isa == ISA_A32 ? "A32" : isa == ISA_T32 ? "T32" : isa == ISA_A64 ? "A64" : "?"; }
