; provides: phi_str_end phi_strcpy phi_strcpy_n phi_strcmp phi_strorder phi_str_has phi_str_has_char phi_int_to_str phi_uint_to_str phi_copy_bytes
;
; Text routines for macOS programs, as x86_64_linux's text.asm and print_int.asm have
; them. Text is the address of zero-terminated bytes. A routine that writes text takes
; x0 = where it goes and w2 = the room there (with its zero), and leaves x0 at the zero
; it wrote, w2 = the room after it, and w3 = 1 if it was cut short (x86's carry), 0 if
; not. They change x0-x17 only, and call nothing but each other.
	.section __TEXT,__text,regular,pure_instructions
	.p2align 2

; x0 = text, w2 = its room -> x0 at the zero that ends it, w2 = the room from there, so
; that copying to x0 with w2 adds to its end. x1 is left as it was.
phi_str_end:
	cmp w2, #1
	b.ls Ltext_end_done
	mov x9, x0
Ltext_end_next:
	ldrb w10, [x9], #1
	cbnz w10, Ltext_end_next
	sub x9, x9, x0
	sub w9, w9, #1              ; its length
	sub w10, w2, #1             ; the most it can move
	cmp w9, w10
	csel w9, w10, w9, hs        ; it fills the room: to the last place
	add x0, x0, x9
	sub w2, w2, w9
Ltext_end_done:
	ret

; x0 = where it goes, x1 = the text, w2 = the room (with its zero): a copy, cut short (w3 = 1)
; when it's longer than the room. Always ends with a zero, unless the room is none.
phi_strcpy:
	mov x9, x1
Ltext_cpy_measure:
	ldrb w10, [x9], #1
	cbnz w10, Ltext_cpy_measure
	sub x9, x9, x1
	sub w9, w9, #1
; the same, with w9 = the text's length already
Ltext_cpy_measured:
	cbz w2, Ltext_cpy_full
	sub w10, w2, #1             ; the most characters there's room for
	mov w3, #0
	cmp w9, w10
	b.ls Ltext_cpy_fits
	mov w9, w10
	mov w3, #1                  ; (there was more: it's longer than the room)
Ltext_cpy_fits:
	sub w2, w10, w9             ; the room left after it
	cbz w9, Ltext_cpy_copied
Ltext_cpy_next:
	ldrb w10, [x1], #1
	strb w10, [x0], #1
	subs w9, w9, #1
	b.ne Ltext_cpy_next
Ltext_cpy_copied:
	strb wzr, [x0]
	ret
Ltext_cpy_full:
	ldrb w3, [x1]               ; (none to copy into: cut if there was any)
	cmp w3, #0
	cset w3, ne
	ret

; phi_strcpy with w9 = the text's length, which it would measure first
phi_strcpy_n:
	b Ltext_cpy_measured

; x0, x1 = two texts -> w0 = 0 if they're the same, 1 if not
phi_strcmp:
	ldrb w9, [x0], #1
	ldrb w10, [x1], #1
	cmp w9, w10
	b.ne Ltext_cmp_differ
	cbnz w9, phi_strcmp
	mov w0, #0
	ret
Ltext_cmp_differ:
	mov w0, #1
	ret

; x0, x1 = two texts -> w0 = -1 if x0's comes first, 1 if x1's does, 0 if they're the same,
; a byte at a time as C's strcmp (a text that's the start of another comes before it)
phi_strorder:
	ldrb w9, [x0], #1
	ldrb w10, [x1], #1
	cmp w9, w10
	b.lo Ltext_order_before
	b.hi Ltext_order_after
	cbnz w9, phi_strorder
	mov w0, #0
	ret
Ltext_order_before:
	mov w0, #-1
	ret
Ltext_order_after:
	mov w0, #1
	ret

; text has 'abc': x0 = the text, x1 = what's looked for -> w0 = 1 if it's somewhere in the
; text (the empty text is in every one), 0 if not
phi_str_has:
	mov x9, #0                  ; how far into what's looked for
Ltext_has_match:
	ldrb w10, [x1, x9]
	cbz w10, Ltext_has_found    ; all of it matched
	ldrb w11, [x0, x9]
	cbz w11, Ltext_has_missing  ; the text ended first
	cmp w10, w11
	b.ne Ltext_has_on
	add x9, x9, #1
	b Ltext_has_match
Ltext_has_on:
	add x0, x0, #1
	b phi_str_has
Ltext_has_found:
	mov w0, #1
	ret
Ltext_has_missing:
	mov w0, #0
	ret

; text has c: x0 = the text, w1 = a character -> w0 = 1 if it's in the text, 0 if not (a zero
; never is)
phi_str_has_char:
	and w1, w1, #0xFF
	cbz w1, Ltext_has_char_not
Ltext_has_char_next:
	ldrb w9, [x0], #1
	cbz w9, Ltext_has_char_not
	cmp w9, w1
	b.ne Ltext_has_char_next
	mov w0, #1
	ret
Ltext_has_char_not:
	mov w0, #0
	ret

; x0 = where it goes, w1 = a number, w2 = the room: the number in decimal, signed
; (phi_int_to_str) or unsigned (phi_uint_to_str), as phi_strcpy writes
phi_uint_to_str:
	mov w11, #0
	mov w10, w1
	b Ltext_int_digits
phi_int_to_str:
	mov w11, #0
	mov w10, w1
	tbz w10, #31, Ltext_int_digits
	neg w10, w10                ; (-2147483648 stays 0x80000000: right, unsigned)
	mov w11, #1
Ltext_int_digits:
	sub sp, sp, #16             ; the digits, on this call's own stack, backwards from the end
	add x1, sp, #12
	strb wzr, [x1]
	mov w12, #10
Ltext_int_next:
	udiv w13, w10, w12
	msub w14, w13, w12, w10
	add w14, w14, #48
	strb w14, [x1, #-1]!
	mov w10, w13
	cbnz w10, Ltext_int_next
	cbz w11, Ltext_int_copy
	mov w14, #45
	strb w14, [x1, #-1]!
Ltext_int_copy:
	add x9, sp, #12
	sub w9, w9, w1              ; how many characters
	mov x15, x30
	bl Ltext_cpy_measured
	add sp, sp, #16
	ret x15

; x0 = where they go, x1 = where they're from, x2 = how many bytes: copied first to last, as
; x86's rep movsb (so where they overlap, with the copy after the original, what's copied
; repeats); 8 at a time where that comes to the same
phi_copy_bytes:
	sub x9, x0, x1
	cmp x9, x2
	b.lo Ltext_copy_bytes       ; (within the bytes ahead: a byte at a time)
	cmp x9, #8
	b.lo Ltext_copy_bytes
Ltext_copy_eight:
	cmp x2, #8
	b.lo Ltext_copy_bytes
	ldr x10, [x1], #8
	str x10, [x0], #8
	sub x2, x2, #8
	b Ltext_copy_eight
Ltext_copy_bytes:
	cbz x2, Ltext_copy_done
	ldrb w10, [x1], #1
	strb w10, [x0], #1
	sub x2, x2, #1
	b Ltext_copy_bytes
Ltext_copy_done:
	ret
