; provides: phi_print_int phi_print_uint phi_print_int_serial phi_print_uint_serial
; requires: phi_print phi_print_serial
;
; A 32-bit number in decimal, signed or unsigned, logged (standard output) or, _serial, to
; standard error: as x86_64_linux's print_int.asm. w0 = the number.
	.section __TEXT,__text,regular,pure_instructions
	.p2align 2

phi_print_int:
	mov w1, #0
	mov w2, #1
	b Lprint_int_any
phi_print_uint:
	mov w1, #0
	mov w2, #0
	b Lprint_int_any
phi_print_int_serial:
	mov w1, #1
	mov w2, #1
	b Lprint_int_any
phi_print_uint_serial:
	mov w1, #1
	mov w2, #0

; w0 = the number, w1 = 1 for standard error, w2 = 1 if it's signed
Lprint_int_any:
	stp x29, x30, [sp, #-48]!
	mov x29, sp
	add x9, sp, #47             ; the digits go backwards from the end of [sp + 16, sp + 48)
	strb wzr, [x9]
	mov w10, w0
	mov w11, #0
	cbz w2, Lprint_int_digits
	tbz w10, #31, Lprint_int_digits
	neg w10, w10                ; (-2147483648 stays 0x80000000: 2147483648 unsigned, as it should)
	mov w11, #1
Lprint_int_digits:
	mov w12, #10
Lprint_int_next:
	udiv w13, w10, w12
	msub w14, w13, w12, w10
	add w14, w14, #48
	strb w14, [x9, #-1]!
	mov w10, w13
	cbnz w10, Lprint_int_next
	cbz w11, Lprint_int_out
	mov w14, #45
	strb w14, [x9, #-1]!
Lprint_int_out:
	mov x0, x9
	cbnz w1, Lprint_int_serial
	bl phi_print
	b Lprint_int_done
Lprint_int_serial:
	bl phi_print_serial
Lprint_int_done:
	ldp x29, x30, [sp], #48
	ret
