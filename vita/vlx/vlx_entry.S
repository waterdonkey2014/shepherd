@ devfail1 "vlx" (Vita-on-Linux eXecution) entry + clone for running the SAME Vita objects (newlib, pthread-embedded,
@ the HOST_VITA Mono runtime, the static AOT images, host.c) as a static ARM32 Linux user-mode program (OrbStack's
@ A32 user-mode emulation on this Mac). No executable memory is created anywhere: this only starts threads.
    .syntax unified
    .arch armv7-a
    .arm
    .text

    .global _lx_start
    .type _lx_start, %function
_lx_start:
    mov     r0, sp                  @ Linux initial stack: argc, argv[], 0, envp[], 0, auxv
    bic     sp, sp, #7
    blx     vlx_boot                @ never returns
1:  b       1b

@ int vlx_clone (unsigned flags, void *child_sp, int *ptid, void *tls, int *ctid, void (*fn) (void *), void *arg)
    .global vlx_clone
    .type vlx_clone, %function
vlx_clone:
    push    {r4-r7, lr}
    ldr     r4, [sp, #20]           @ ctid
    ldr     r5, [sp, #24]           @ fn
    ldr     r6, [sp, #28]           @ arg
    mov     r7, #120                @ __NR_clone (ARM order: flags, newsp, ptid, tls, ctid)
    svc     #0
    cmp     r0, #0
    popne   {r4-r7, pc}             @ parent (tid) or error (-errno)
    mov     r0, r6                  @ child: sp = child_sp; r5/r6 survive the clone
    blx     r5
    mov     r0, #0
    mov     r7, #1                  @ __NR_exit: this thread only (CLONE_CHILD_CLEARTID wakes the joiner)
    svc     #0
2:  b       2b

@ rt_sigreturn trampoline for SA_RESTORER
    .global vlx_sigreturn
    .type vlx_sigreturn, %function
vlx_sigreturn:
    mov     r7, #173
    svc     #0
