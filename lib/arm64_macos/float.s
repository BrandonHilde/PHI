; provides: phi_float_to_str phi_print_float phi_print_float_serial
; requires: phi_uint64_to_str phi_int_to_str phi_strcpy phi_print phi_print_serial
;
; Floating point numbers (f64; f32s are made f64s to be worked on) as text, for macOS
; programs: x86_64_linux's float.asm step for step, so the same number is the same text.

; x0 = where it goes, d0 = the number, w2 = the room: decimal text, up to 6 digits after the
; point, without zeros at the end (2.5, 3, -0.125), and with an exponent from 10^12 up and
; under 0.0001 (1.5e20, 2.5e-7), or nan, inf, -inf; as text.s's phi_strcpy writes
	.section __TEXT,__text,regular,pure_instructions
	.p2align 2
phi_float_to_str:
	stp x29, x30, [sp, #-128]!
	mov x29, sp
	stp x19, x20, [sp, #16]
	stp x21, x22, [sp, #32]
	mov x19, x0                 ; the destination
	mov w20, w2                 ; its room
	add x21, sp, #48            ; the text, on this call's own stack: [sp + 48, sp + 112)
	mov w22, #0                 ; the exponent
	fcmp d0, d0
	b.vs Lfloat_nan             ; not a number has no sign
	fmov x9, d0
	tbz x9, #63, Lfloat_positive
	mov w10, #45
	strb w10, [x21], #1
Lfloat_positive:
	fabs d0, d0
	fmov x9, d0
	mov x10, #0x7FF0000000000000
	cmp x9, x10
	b.eq Lfloat_infinity

	; from 10^12 up, or under 10^-4 (but not 0), it gets an exponent
	adrp x9, Lfloat_constants@PAGE
	add x9, x9, Lfloat_constants@PAGEOFF
	ldp d1, d2, [x9]            ; 10^12, 10^-4
	ldp d3, d4, [x9, #16]       ; 10, 1
	ldr d5, [x9, #32]           ; 10^6
	fcmp d0, d1
	b.ge Lfloat_down
	fcmp d0, #0.0
	b.eq Lfloat_fixed
	fcmp d0, d2
	b.ge Lfloat_fixed
	; between 1 and 10, counting the factors of ten taken out
Lfloat_down:
	fcmp d0, d3
	b.lt Lfloat_up
	fdiv d0, d0, d3
	add w22, w22, #1
	b Lfloat_down
Lfloat_up:
	fcmp d0, d4
	b.ge Lfloat_fixed
	fmul d0, d0, d3
	sub w22, w22, #1
	b Lfloat_up

Lfloat_fixed:
	; the whole number of millionths, rounded (to even, as fistp rounds)
	fmul d0, d0, d5
	fcvtns x9, d0
	mov x10, #16960
	movk x10, #15, lsl #16      ; 1000000
	udiv x11, x9, x10           ; the whole part
	msub x12, x11, x10, x9      ; the millionths
	str x12, [sp, #112]         ; (kept across the call)
	mov x0, x21
	mov x1, x11
	mov w2, #24
	bl phi_uint64_to_str        ; x0 ends at its zero
	mov x21, x0
	ldr x12, [sp, #112]
	cbz x12, Lfloat_exponent

	; the point, then six digits (with their zeros in front), less the zeros at the end
	mov w10, #46
	strb w10, [x21], #1
	mov x13, #10
	mov x14, #6
Lfloat_digit:
	udiv x15, x12, x13
	msub x16, x15, x13, x12
	add w16, w16, #48
	sub x14, x14, #1
	strb w16, [x21, x14]
	mov x12, x15
	cbnz x14, Lfloat_digit
	add x21, x21, #6
Lfloat_trim:
	ldrb w10, [x21, #-1]
	cmp w10, #48
	b.ne Lfloat_exponent
	sub x21, x21, #1
	b Lfloat_trim

Lfloat_exponent:
	cbz w22, Lfloat_done
	mov w10, #101
	strb w10, [x21], #1
	mov x0, x21
	mov w1, w22
	mov w2, #12
	bl phi_int_to_str
	mov x21, x0
	b Lfloat_done

Lfloat_nan:
	mov w10, #110               ; nan
	strb w10, [x21], #1
	mov w10, #97
	strb w10, [x21], #1
	mov w10, #110
	strb w10, [x21], #1
	b Lfloat_done
Lfloat_infinity:
	mov w10, #105               ; inf
	strb w10, [x21], #1
	mov w10, #110
	strb w10, [x21], #1
	mov w10, #102
	strb w10, [x21], #1

Lfloat_done:
	strb wzr, [x21]
	mov x0, x19
	add x1, sp, #48
	mov w2, w20
	bl phi_strcpy
	ldp x19, x20, [sp, #16]
	ldp x21, x22, [sp, #32]
	ldp x29, x30, [sp], #128
	ret

; d0 = a number, logged (or, _serial, to standard error)
phi_print_float:
	mov w4, #0
	b Lfloat_print
phi_print_float_serial:
	mov w4, #1
Lfloat_print:
	stp x29, x30, [sp, #-80]!
	mov x29, sp
	str x19, [sp, #16]
	mov w19, w4
	add x0, sp, #24
	mov w2, #48
	bl phi_float_to_str
	add x0, sp, #24
	cbnz w19, Lfloat_print_serial
	bl phi_print
	b Lfloat_print_done
Lfloat_print_serial:
	bl phi_print_serial
Lfloat_print_done:
	ldr x19, [sp, #16]
	ldp x29, x30, [sp], #80
	ret

	.section __TEXT,__const
	.p2align 3
Lfloat_constants:
	.double 1.0e12, 1.0e-4, 10.0, 1.0, 1000000.0
