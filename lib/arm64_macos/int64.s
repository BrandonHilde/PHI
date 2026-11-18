; provides: phi_int64_to_str phi_uint64_to_str phi_print_int64 phi_print_uint64 phi_print_int64_serial phi_print_uint64_serial
; requires: phi_strcpy_n phi_print phi_print_serial
;
; 64-bit numbers (i64, u64) as text, for macOS programs: x86_64_linux's int64.asm's, but an
; i64 is in one register here, so its arithmetic is the code's own.

; x0 = where it goes, x1 = a number, w2 = the room: the number in decimal, signed
; (phi_int64_to_str) or unsigned, as text.s's phi_strcpy writes
	.section __TEXT,__text,regular,pure_instructions
	.p2align 2
phi_uint64_to_str:
	mov w11, #0
	mov x10, x1
	b Lint64_digits
phi_int64_to_str:
	mov w11, #0
	mov x10, x1
	tbz x10, #63, Lint64_digits
	neg x10, x10                ; (the smallest i64 stays itself: right, unsigned)
	mov w11, #1
Lint64_digits:
	sub sp, sp, #32             ; the digits, on this call's own stack, backwards from the end
	add x1, sp, #24
	strb wzr, [x1]
	mov x12, #10
Lint64_next:
	udiv x13, x10, x12
	msub x14, x13, x12, x10
	add w14, w14, #48
	strb w14, [x1, #-1]!
	mov x10, x13
	cbnz x10, Lint64_next
	cbz w11, Lint64_copy
	mov w14, #45
	strb w14, [x1, #-1]!
Lint64_copy:
	add x9, sp, #24
	sub w9, w9, w1              ; how many characters
	mov x15, x30
	bl phi_strcpy_n
	add sp, sp, #32
	ret x15

; x0 = a number, logged (or, _serial, to standard error): signed, or unsigned
phi_print_int64:
	mov w4, #1
	mov w5, #0
	b Lint64_print
phi_print_uint64:
	mov w4, #0
	mov w5, #0
	b Lint64_print
phi_print_int64_serial:
	mov w4, #1
	mov w5, #1
	b Lint64_print
phi_print_uint64_serial:
	mov w4, #0
	mov w5, #1
; w4 = 1 if it's signed, w5 = 1 for standard error
Lint64_print:
	stp x29, x30, [sp, #-64]!
	mov x29, sp
	str x19, [sp, #16]
	mov w19, w5
	mov x1, x0
	add x0, sp, #32
	mov w2, #24
	cbz w4, Lint64_print_unsigned
	bl phi_int64_to_str
	b Lint64_print_out
Lint64_print_unsigned:
	bl phi_uint64_to_str
Lint64_print_out:
	add x0, sp, #32
	cbnz w19, Lint64_print_serial
	bl phi_print
	b Lint64_print_done
Lint64_print_serial:
	bl phi_print_serial
Lint64_print_done:
	ldr x19, [sp, #16]
	ldp x29, x30, [sp], #64
	ret
