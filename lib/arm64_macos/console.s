; provides: phi_print phi_print_serial phi_print_char phi_print_char_serial phi_console_putc phi_flush phi_write_all phi_strlen
;
; Output for macOS programs (Apple Silicon), as x86_64_linux's console.asm: `log` output
; collects in a buffer and goes to standard output in one write when phi_flush runs (after
; every `log` statement); `debug` goes to standard error, after what was logged before it.
;
; These, like all of lib/arm64_macos, are ordinary AAPCS64 functions: arguments in x0 up, a
; result in x0; x0-x17 may change, x19-x29 and sp are kept. So they call libSystem directly.
	.section __TEXT,__text,regular,pure_instructions
	.p2align 2

; x0 = zero-terminated text, to standard output (buffered)
phi_print:
	stp x29, x30, [sp, #-32]!
	mov x29, sp
	str x19, [sp, #16]
	mov x19, x0
Lconsole_print_next:
	ldrb w0, [x19], #1
	cbz w0, Lconsole_print_done
	bl phi_console_putc
	b Lconsole_print_next
Lconsole_print_done:
	ldr x19, [sp, #16]
	ldp x29, x30, [sp], #32
	ret

; w0 = one character, into the buffer (written out when it's full)
phi_console_putc:
	adrp x16, phi_console_length@PAGE
	add x16, x16, phi_console_length@PAGEOFF
	ldr w9, [x16]
	adrp x17, phi_console_buffer@PAGE
	add x17, x17, phi_console_buffer@PAGEOFF
	strb w0, [x17, w9, uxtw]
	add w9, w9, #1
	str w9, [x16]
	cmp w9, #4096
	b.hs phi_flush
	ret

; write what's buffered to standard output
phi_flush:
	stp x29, x30, [sp, #-16]!
	mov x29, sp
	adrp x16, phi_console_length@PAGE
	add x16, x16, phi_console_length@PAGEOFF
	ldr w2, [x16]
	cbz w2, Lconsole_flush_done
	str wzr, [x16]
	mov w0, #1
	adrp x1, phi_console_buffer@PAGE
	add x1, x1, phi_console_buffer@PAGEOFF
	bl phi_write_all
Lconsole_flush_done:
	ldp x29, x30, [sp], #16
	ret

; x0 = zero-terminated text, to standard error (the `debug` statement)
phi_print_serial:
	stp x29, x30, [sp, #-32]!
	mov x29, sp
	str x19, [sp, #16]
	mov x19, x0
	bl phi_flush                ; what was logged before it comes first
	mov x0, x19
	bl phi_strlen
	mov w2, w0
	mov x1, x19
	mov w0, #2
	bl phi_write_all
	ldr x19, [sp, #16]
	ldp x29, x30, [sp], #32
	ret

; w0 = a character, logged (or, _serial, to standard error)
phi_print_char:
	stp x29, x30, [sp, #-16]!
	mov x29, sp
	bl phi_console_putc
	ldp x29, x30, [sp], #16
	ret

phi_print_char_serial:
	stp x29, x30, [sp, #-32]!
	mov x29, sp
	strb w0, [sp, #16]
	strb wzr, [sp, #17]
	add x0, sp, #16
	bl phi_print_serial
	ldp x29, x30, [sp], #32
	ret

; x0 = zero-terminated text -> w0 = how many bytes before its zero
phi_strlen:
	mov x9, x0
Lconsole_strlen_next:
	ldrb w10, [x9], #1
	cbnz w10, Lconsole_strlen_next
	sub x0, x9, x0
	sub w0, w0, #1
	ret

; w0 = a file, x1 = data, w2 = its length: write all of it (a pipe can take less at a time)
; -> w0 = 1 if it was, 0 if writing failed
phi_write_all:
	stp x29, x30, [sp, #-48]!
	mov x29, sp
	stp x19, x20, [sp, #16]
	str x21, [sp, #32]
	mov w19, w0
	mov x20, x1
	mov w21, w2
Lconsole_write_next:
	cmp w21, #0
	b.le Lconsole_write_ok
	mov w0, w19
	mov x1, x20
	mov w2, w21
	bl _write
	cmp x0, #0
	b.le Lconsole_write_failed
	add x20, x20, x0
	sub w21, w21, w0
	b Lconsole_write_next
Lconsole_write_ok:
	mov w0, #1
	b Lconsole_write_done
Lconsole_write_failed:
	mov w0, #0
Lconsole_write_done:
	ldp x19, x20, [sp, #16]
	ldr x21, [sp, #32]
	ldp x29, x30, [sp], #48
	ret

	.section __DATA,__data
	.p2align 2
phi_console_length:
	.long 0
	.zerofill __DATA,__bss,phi_console_buffer,4096,4
