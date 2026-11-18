; provides: phi_f32_to_f16
;
; f16s (halves) for macOS programs. An f16 is worked on as an f64; it is read with fcvt (any
; f16 is an f32, exactly), and written rounded to an f32 and then, here, to an f16 as x86's
; float.asm rounds one (half up, not to even), so a program's halves are the same on both.

; w0 = an f32's bits -> w0 = the nearest half's; too big is infinity. Changes only w0-w3
; (the code calls it between its own instructions).
	.section __TEXT,__text,regular,pure_instructions
	.p2align 2
phi_f32_to_f16:
	lsr w1, w0, #16
	and w1, w1, #0x8000         ; the sign
	ubfx w2, w0, #23, #8        ; the exponent
	and w0, w0, #0x7FFFFF       ; the fraction
	cmp w2, #255
	b.eq 5f
	sub w2, w2, #112            ; 127 - 15
	cmp w2, #31
	b.ge 6f
	cmp w2, #0
	b.le 3f
	; rounded to 10 bits of fraction; a carry rounds up into the exponent, as it should
	add w0, w0, #0x1000
	lsr w0, w0, #13
	add w0, w0, w2, lsl #10
	mov w3, #0x7C00
	cmp w0, w3
	b.hs 6f
	b 7f
3:	; a subnormal half: the leading one goes in, shifted right by 14 - exponent, rounded
	cmn w2, #10
	b.lt 4f
	orr w0, w0, #0x800000
	neg w2, w2
	add w2, w2, #14
	mov w3, #1
	lsl w3, w3, w2
	lsr w3, w3, #1
	add w0, w0, w3
	lsr w0, w0, w2
	b 7f
4:	mov w0, #0
	b 7f
5:	cmp w0, #0
	mov w0, #0x7C00             ; infinity ...
	b.eq 7f
	orr w0, w0, #0x200          ; ... or not a number
	b 7f
6:	mov w0, #0x7C00
7:	orr w0, w0, w1
	ret
