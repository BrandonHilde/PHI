; provides: phi_exit phi_log_take phi_log_give phi_argc phi_argv
; requires: phi_flush
; init: phi_macos_start
;
; What a program asks of the system, for macOS programs: answered by libSystem (macOS has
; no system calls a program may make itself; its numbers change from release to release).
	.section __TEXT,__text,regular,pure_instructions
	.p2align 2

; x0 = argc, x1 = argv, from _main: kept for OS.Arguments
phi_macos_start:
	adrp x16, phi_argc@PAGE
	add x16, x16, phi_argc@PAGEOFF
	str x0, [x16]
	adrp x16, phi_argv@PAGE
	add x16, x16, phi_argv@PAGEOFF
	str x1, [x16]
	ret

; w0 = the exit code: what's logged written out, then the program ends
phi_exit:
	mov w19, w0
	bl phi_flush
	mov w0, w19
	bl _exit

; the log's lock, taken and given back around a log statement: one thread, for now
phi_log_take:
phi_log_give:
	ret

	.section __DATA,__data
	.p2align 3
phi_argc:
	.quad 0
phi_argv:
	.quad 0
