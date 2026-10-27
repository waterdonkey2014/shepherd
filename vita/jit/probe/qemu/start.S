/* Shepherd JIT probe, qemu encoding check (run.sh): bare-metal entry for qemu-system-arm -M vexpress-a9 -cpu cortex-a9 (the Vita's
 * core). ARM state, SVC mode, MMU off: stack, VFP/NEON on (the vitasdk compiler defaults to hard-float NEON), .bss cleared, qmain. */
	.syntax unified
	.section .text.start, "ax"
	.arm
	.global _start
_start:
	ldr	sp, =__stack_top
	mrc	p15, 0, r0, c1, c0, 2		@ CPACR: cp10 / cp11 full access
	orr	r0, r0, #(0xF << 20)
	mcr	p15, 0, r0, c1, c0, 2
	isb
	mov	r0, #0x40000000			@ FPEXC.EN
	vmsr	fpexc, r0
	ldr	r0, =__bss_start
	ldr	r1, =__bss_end
	mov	r2, #0
1:	cmp	r0, r1
	strlo	r2, [r0], #4
	blo	1b
	ldr	r0, =qmain
	blx	r0
2:	b	2b
