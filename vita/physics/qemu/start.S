@ Bare-metal entry for the QEMU ARM32 run of libshepphys_vita.a (ours). SVC mode, IRQ/FIQ off; enables VFP/NEON;
@ zeroes .bss; then C (harness.c) sets up an identity MMU map and runs the T1 scenarios; exits through semihosting.
    .syntax unified
    .arch armv7-a
    .fpu neon
    .section .text.start, "ax"
    .global _start
    .arm
_start:
    cpsid   if
    ldr     sp, =__stack_top
    mrc     p15, 0, r0, c1, c0, 2          @ CPACR: full access to cp10 / cp11 (VFP, NEON)
    orr     r0, r0, #(0xF << 20)
    mcr     p15, 0, r0, c1, c0, 2
    isb
    mov     r0, #0x40000000                @ FPEXC.EN
    vmsr    fpexc, r0
    ldr     r0, =__bss_start
    ldr     r1, =__bss_end
    mov     r2, #0
1:  cmp     r0, r1
    strlo   r2, [r0], #4
    blo     1b
    blx     harness_main
2:  b       2b
