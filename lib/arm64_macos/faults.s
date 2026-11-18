; provides: phi_fault_register
; requires: phi_fault_entry phi_main_thread
; init: phi_fault_register
;
; A program the CPU stops (an empty object or a null pointer used, memory that isn't there,
; the stack run out, an instruction refused) throws an error, as x86_64_linux's
; system.asm and throw.asm have it: its signals come to a handler on a spare stack of its
; own (a stack that has run out has no room for one), which sends it to phi_fault_entry.
	.section __TEXT,__text,regular,pure_instructions
	.p2align 2

phi_fault_register:
	stp x29, x30, [sp, #-48]!
	mov x29, sp
	; the spare stack: sigaltstack(&{ ss_sp, ss_size, ss_flags }, 0)
	adrp x9, phi_fault_spare@PAGE
	add x9, x9, phi_fault_spare@PAGEOFF
	str x9, [sp, #16]
	mov x9, #65536
	str x9, [sp, #24]
	str wzr, [sp, #32]
	add x0, sp, #16
	mov x1, #0
	bl _sigaltstack
	; (the first thread's block says where its spare stack's top is, as each thread's does)
	adrp x9, phi_main_thread@PAGE
	add x9, x9, phi_main_thread@PAGEOFF
	adrp x10, phi_fault_spare@PAGE
	add x10, x10, phi_fault_spare@PAGEOFF
	add x10, x10, #16, lsl #12
	str x10, [x9, #1016]
	; sigaction(signal, &{ handler, mask 0, SA_SIGINFO | SA_ONSTACK }, 0) for each
	adrp x9, Lfault_handler@PAGE
	add x9, x9, Lfault_handler@PAGEOFF
	str x9, [sp, #16]
	str wzr, [sp, #24]
	mov w9, #65
	str w9, [sp, #28]
	mov w0, #11                 ; SIGSEGV
	add x1, sp, #16
	mov x2, #0
	bl _sigaction
	mov w0, #10                 ; SIGBUS
	add x1, sp, #16
	mov x2, #0
	bl _sigaction
	mov w0, #4                  ; SIGILL
	add x1, sp, #16
	mov x2, #0
	bl _sigaction
	mov w0, #8                  ; SIGFPE
	add x1, sp, #16
	mov x2, #0
	bl _sigaction
	mov w0, #5                  ; SIGTRAP
	add x1, sp, #16
	mov x2, #0
	bl _sigaction
	ldp x29, x30, [sp], #48
	ret

; w0 = the signal, x1 = its siginfo (si_addr at 24), x2 = the ucontext (uc_mcontext at 48:
; in it x0 at 16, x1 at 24, sp at 264, pc at 272). The stopped code carries on at
; throw.s's phi_fault_entry, with w0 the kind of error and x1 where it happened, when the
; handler returns; a stack that has run out carries on at the top of the spare stack.
Lfault_handler:
	ldr x9, [x1, #24]           ; the memory it used
	ldr x10, [x2, #48]
	ldr x11, [x10, #272]        ; where it was
	ldr x12, [x10, #264]        ; and its stack pointer
	mov w13, #5                 ; ErrorKind.ACCESS
	cmp w0, #11                 ; SIGSEGV
	b.eq Lfault_memory
	cmp w0, #10                 ; SIGBUS
	b.ne Lfault_send
Lfault_memory:
	cmp x9, #4096
	b.hs Lfault_stack
	mov w13, #4                 ; ErrorKind.EMPTY
	b Lfault_send
Lfault_stack:
	; just by the stack pointer (64 KB below it to 16 KB above): the stack has run out
	sub x14, x12, #16, lsl #12
	cmp x9, x14
	b.lo Lfault_send
	add x14, x12, #4, lsl #12
	cmp x9, x14
	b.hs Lfault_send
	; the thread's spare stack: its block (the stopped code's x28, at 240 in the context) says
	; where its top is (+1016), 64 KB above its bottom
	ldr x15, [x10, #240]
	ldr x15, [x15, #1016]
	cbz x15, Lfault_again
	sub x14, x15, #16, lsl #12
	cmp x12, x14
	b.lo Lfault_to_spare
	cmp x12, x15
	b.ls Lfault_again           ; on the spare stack itself: there's nowhere further to go
Lfault_to_spare:
	str x15, [x10, #264]
	mov w13, #8                 ; ErrorKind.STACK
Lfault_send:
	adrp x14, phi_fault_entry@PAGE
	add x14, x14, phi_fault_entry@PAGEOFF
	cmp x11, x14
	b.eq Lfault_again           ; the entry itself was stopped: it would only be again
	str x13, [x10, #16]
	str x11, [x10, #24]
	str x14, [x10, #272]
	ret
Lfault_again:
	movn w0, #0
	bl __exit

	.zerofill __DATA,__bss,phi_fault_spare,65536,4
