; provides: phi_fault_register phi_line_at
; requires: phi_print phi_print_int phi_exit
; init: phi_fault_register
;
; A program the CPU stops (an empty object or a null pointer used, memory that isn't there,
; the stack run out) prints why, with its line, and ends, as x86_64_linux's throw.asm does
; for one that isn't caught: CR LF, "PHI: ", the message, " on line N in FILE", and exit
; code -1. Its signals come to a handler on a spare stack of its own (a stack that has run
; out has no room for one). Catching them (if call ... throws) is to come.
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

; w0 = the signal, x1 = its siginfo, x2 = the ucontext (uc_mcontext at 48: its pc at 272,
; its sp at 264). It never comes back, so it uses x19 up as it likes.
Lfault_handler:
	mov w19, w0
	ldr x20, [x1, #24]          ; si_addr: the memory it used
	ldr x21, [x2, #48]
	ldr x22, [x21, #272]        ; where it was
	ldr x23, [x21, #264]        ; and its stack pointer
	adrp x24, Lfault_refused@PAGE
	add x24, x24, Lfault_refused@PAGEOFF
	cmp w19, #11
	b.eq Lfault_memory
	cmp w19, #10
	b.ne Lfault_say
Lfault_memory:
	cmp x20, #4096
	b.hs Lfault_stack
	adrp x24, Lfault_empty@PAGE
	add x24, x24, Lfault_empty@PAGEOFF
	b Lfault_say
Lfault_stack:
	; just by the stack pointer (64 KB below it to 16 KB above): the stack has run out
	sub x9, x23, #16, lsl #12
	cmp x20, x9
	b.lo Lfault_say
	add x9, x23, #4, lsl #12
	cmp x20, x9
	b.hs Lfault_say
	adrp x24, Lfault_stack_out@PAGE
	add x24, x24, Lfault_stack_out@PAGEOFF
Lfault_say:
	adrp x0, Lfault_prefix@PAGE
	add x0, x0, Lfault_prefix@PAGEOFF
	bl phi_print
	mov x0, x24
	bl phi_print
	mov x0, x22
	bl phi_line_at
	cbz w0, Lfault_end
	mov w25, w0
	mov x26, x1
	adrp x0, Lfault_on_line@PAGE
	add x0, x0, Lfault_on_line@PAGEOFF
	bl phi_print
	mov w0, w25
	bl phi_print_int
	adrp x0, Lfault_in@PAGE
	add x0, x0, Lfault_in@PAGEOFF
	bl phi_print
	mov x0, x26
	bl phi_print
Lfault_end:
	adrp x0, Lfault_newline@PAGE
	add x0, x0, Lfault_newline@PAGEOFF
	bl phi_print
	movn w0, #0
	b phi_exit

; x0 = an address in the program's code -> w0 = the line it's on, x1 = its file's name; 0 and
; 0 when it isn't PHI code (the runtime) or isn't known (the program's phi_lines: a count,
; then each line's address, line and file, 8 bytes each)
phi_line_at:
	adrp x9, phi_lines@PAGE
	add x9, x9, phi_lines@PAGEOFF
	ldr x10, [x9], #8
	mov x11, #0                 ; the last mark at or before the address
Lfault_line_next:
	cbz x10, Lfault_line_found
	ldr x12, [x9]
	cmp x12, x0
	b.hi Lfault_line_found
	mov x11, x9
	add x9, x9, #24
	sub x10, x10, #1
	b Lfault_line_next
Lfault_line_found:
	mov w0, #0
	mov x1, #0
	cbz x11, Lfault_line_done
	ldr w0, [x11, #8]
	cbz w0, Lfault_line_done
	ldr x1, [x11, #16]
Lfault_line_done:
	ret

	.section __TEXT,__const
Lfault_prefix:
	.byte 13, 10, 80, 72, 73, 58, 32, 0          ; CR LF "PHI: "
Lfault_empty:
	.asciz "an empty object or a null pointer was used"
Lfault_stack_out:
	.asciz "the stack ran out: calls too deep"
Lfault_refused:
	.asciz "memory that is not there was used, or the CPU refused an instruction"
Lfault_on_line:
	.asciz " on line "
Lfault_in:
	.asciz " in "
Lfault_newline:
	.byte 10, 0
	.zerofill __DATA,__bss,phi_fault_spare,65536,4
