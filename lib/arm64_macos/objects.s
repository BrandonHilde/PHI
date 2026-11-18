; provides: phi_str_room phi_grow_store phi_array_grow phi_text_array_grow
; requires: phi_out_of_memory phi_strlen phi_strcpy phi_strcpy_n phi_int_to_str phi_uint_to_str phi_copy_bytes
;
; What PHI allocates itself, for macOS programs, as the x86 generator's routines have it
; (X86Generator: EmitStrRoomRoutine, EmitGrowStoreAt, EmitArrayGrowRoutine,
; EmitTextArrayGrowRoutine; out_of_memory.asm): a str that grows, and arrays without a size.
; The heap is the program's own (memory.program_heap): phi_heap_alloc and phi_heap_free are
; the program's Heap.Alloc and Heap.Free, which the generator names so (PHI's methods: the
; argument at [sp], 8 bytes, x19-x29 kept).
;
; A str that grows is a slot: its text's address, then its room (a word; its top bit says the
; text is in the program's data, not the heap, and the bit under it that it's a caller's str
; with a size, whose text never moves). An array without a size is a view: its elements'
; address, how many it has (+8), its room (+12).
	.section __TEXT,__text,regular,pure_instructions
	.p2align 2

; x0 = where a str's text's address is, x1 = its room's word, w2 = the bytes wanted in it (with
; the zero) -> x0 = its text, with room for that many (or as many as the heap had), w1 = its
; room, x2 = its old text, to free once nothing reads from it (0 if it didn't move)
phi_str_room:
	stp x29, x30, [sp, #-64]!
	mov x29, sp
	stp x19, x20, [sp, #16]
	stp x21, x22, [sp, #32]
	str x23, [sp, #48]
	mov x19, x0
	mov x20, x1
	mov x22, #0                 ; nothing to free
	mov w9, #0x3FFFFFFF         ; never more than a str's max (and its zero)
	cmp w2, w9
	csel w2, w9, w2, hi
	ldr w10, [x20]
	tst w10, #0x40000000
	b.ne Lobj_room_fits         ; a caller's str with a size: it stays, and what doesn't fit is an error
	and w10, w10, #0x3FFFFFFF   ; its room
	cmp w2, w10
	b.ls Lobj_room_fits
	; the new room: twice the old, and at least what's wanted (and 16), up to the max
	lsl w21, w10, #1
	cmp w21, w2
	csel w21, w2, w21, lo
	cmp w21, #16
	mov w11, #16
	csel w21, w11, w21, lo
	cmp w21, w9
	csel w21, w9, w21, hi
	sub sp, sp, #16
	str x21, [sp]
	bl phi_heap_alloc
	add sp, sp, #16
	cbz x0, Lobj_room_fits      ; no memory: it keeps the room it has
	mov x23, x0
	; the text so far goes along, then the old place is the caller's to free
	ldr x1, [x19]
	cbz x1, Lobj_room_copied
	mov w2, w21
	bl phi_strcpy
Lobj_room_copied:
	ldr x22, [x19]
	ldr w10, [x20]
	tst w10, #0x80000000
	csel x22, xzr, x22, ne      ; (one in the program's data isn't the heap's)
	str x23, [x19]
	str w21, [x20]
Lobj_room_fits:
	ldr w1, [x20]
	and w1, w1, #0x3FFFFFFF
	ldr x0, [x19]
	mov x2, x22
	ldp x19, x20, [sp, #16]
	ldp x21, x22, [sp, #32]
	ldr x23, [sp, #48]
	ldp x29, x30, [sp], #64
	ret

; A value put in a str that grows (the generator's EmitGrowStoreAt): x0 = its slot, x1 = its
; room's word, x2 = the value (a text's address, or a number to write out), w3 = 1 for text
; (else a number) + 2 to add it after what's there + 4 for an unsigned number, x4 = where the
; method keeps its length (plus 1, 0 for not known), or 0, w5 = the value's length plus 1 when
; it's known (text written in the program), or 0 -> w0 = 1 if it was cut short
phi_grow_store:
	stp x29, x30, [sp, #-96]!
	mov x29, sp
	stp x19, x20, [sp, #16]
	stp x21, x22, [sp, #32]
	stp x23, x24, [sp, #48]
	stp x25, x26, [sp, #64]
	mov x19, x0
	mov x20, x1
	mov x21, x2
	mov w22, w3
	mov x23, x4
	; how long the value is, with its zero
	mov w24, #12                ; (the most a number takes)
	tbz w22, #0, Lobj_store_measured
	mov w24, w5
	cbnz w24, Lobj_store_measured
	mov x0, x21
	bl phi_strlen
	add w24, w0, #1
Lobj_store_measured:
	mov w2, w24
	tbz w22, #1, Lobj_store_room
	; after what's there: how long it is now, where the value goes
	ldr x0, [x19]
	adrp x9, Lobj_empty@PAGE
	add x9, x9, Lobj_empty@PAGEOFF
	cmp x0, #0
	csel x0, x9, x0, eq
	cbz x23, Lobj_store_measure
	; as long as the method last knew it to be, when that's still where it ends
	ldr w9, [x23]
	subs w9, w9, #1
	b.mi Lobj_store_measure
	ldrb w10, [x0, w9, uxtw]
	cbnz w10, Lobj_store_measure
	mov w0, w9
	b Lobj_store_at
Lobj_store_measure:
	bl phi_strlen
Lobj_store_at:
	mov w25, w0
	add w2, w0, w24
Lobj_store_room:
	mov x0, x19
	mov x1, x20
	bl phi_str_room
	mov x26, x2                 ; the old text, to free after
	tbz w22, #1, Lobj_store_copy
	; on from its end, with that much less room; all its room when the heap had no more is
	; the zero's, and the value is cut
	cmp w25, w1
	b.lo Lobj_store_some
	sub w25, w1, #1
Lobj_store_some:
	add x0, x0, w25, sxtw
	sub w1, w1, w25
Lobj_store_copy:
	mov w2, w1
	tbz w22, #0, Lobj_store_number
	mov x1, x21
	sub w9, w24, #1             ; its length, measured above
	bl phi_strcpy_n
	b Lobj_store_copied
Lobj_store_number:
	mov w1, w21
	tbnz w22, #2, Lobj_store_unsigned
	bl phi_int_to_str
	b Lobj_store_copied
Lobj_store_unsigned:
	bl phi_uint_to_str
Lobj_store_copied:
	mov w24, w3                 ; whether it was cut
	cbz x23, Lobj_store_free
	; how long it is now: the copy left x0 at the zero it wrote
	ldr x9, [x19]
	sub x9, x0, x9
	add w9, w9, #1
	str w9, [x23]
Lobj_store_free:
	cbz x26, Lobj_store_done
	sub sp, sp, #16
	str x26, [sp]
	bl phi_heap_free            ; its old text
	add sp, sp, #16
Lobj_store_done:
	mov w0, w24
	ldp x19, x20, [sp, #16]
	ldp x21, x22, [sp, #32]
	ldp x23, x24, [sp, #48]
	ldp x25, x26, [sp, #64]
	ldp x29, x30, [sp], #96
	ret

; Room in an array that grows: x0 = its view, w1 = how many it's to hold, w2 = the bytes of each.
; The new room is twice the old, at least what's needed, and at least 4; the elements so far
; move over (their objects, if any, go with them).
phi_array_grow:
	ldr w9, [x0, #12]
	cmp w1, w9
	b.hi Lobj_grow
	ret
Lobj_grow:
	stp x29, x30, [sp, #-48]!
	mov x29, sp
	stp x19, x20, [sp, #16]
	stp x21, x22, [sp, #32]
	mov x19, x0
	mov w20, w2
	lsl w21, w9, #1
	cmp w21, w1
	csel w21, w1, w21, lo
	cmp w21, #4
	mov w9, #4
	csel w21, w9, w21, lo
	umull x9, w21, w20
	sub sp, sp, #16
	str x9, [sp]
	bl phi_heap_alloc
	add sp, sp, #16
	cbz x0, phi_out_of_memory
	mov x22, x0
	ldr x1, [x19]
	ldr w2, [x19, #8]
	umull x2, w2, w20
	bl phi_copy_bytes
	sub sp, sp, #16
	ldr x9, [x19]
	str x9, [sp]
	bl phi_heap_free            ; (freeing 0 does nothing)
	add sp, sp, #16
	str x22, [x19]
	str w21, [x19, #12]
	ldp x19, x20, [sp, #16]
	ldp x21, x22, [sp, #32]
	ldp x29, x30, [sp], #48
	ret

; Room in a str array that grows: x0 = its view, w1 = how many. Its room is each text's
; address, then each one's room (8 bytes each); a new slot is '' in the program's data.
phi_text_array_grow:
	ldr w9, [x0, #12]
	cmp w1, w9
	b.hi Lobj_text_grow
	ret
Lobj_text_grow:
	stp x29, x30, [sp, #-48]!
	mov x29, sp
	stp x19, x20, [sp, #16]
	stp x21, x22, [sp, #32]
	mov x19, x0
	lsl w21, w9, #1
	cmp w21, w1
	csel w21, w1, w21, lo
	cmp w21, #4
	mov w9, #4
	csel w21, w9, w21, lo
	lsl x9, x21, #4             ; a text and a room for each
	sub sp, sp, #16
	str x9, [sp]
	bl phi_heap_alloc
	add sp, sp, #16
	cbz x0, phi_out_of_memory
	mov x22, x0
	; every slot empty to begin with ('', in the data), then the ones it has move over
	adrp x10, Lobj_empty@PAGE
	add x10, x10, Lobj_empty@PAGEOFF
	mov x11, #0x80000000
	add x12, x22, x21, lsl #3   ; the rooms
	mov x13, #0
Lobj_text_fill:
	str x10, [x22, x13, lsl #3]
	str x11, [x12, x13, lsl #3]
	add x13, x13, #1
	cmp x13, x21
	b.lo Lobj_text_fill
	ldr x20, [x19]
	cbz x20, Lobj_text_moved
	; their texts' addresses, then their rooms
	mov x0, x22
	mov x1, x20
	ldr w2, [x19, #8]
	lsl x2, x2, #3
	bl phi_copy_bytes
	add x0, x22, x21, lsl #3
	ldr w9, [x19, #12]
	add x1, x20, x9, lsl #3
	ldr w2, [x19, #8]
	lsl x2, x2, #3
	bl phi_copy_bytes
	sub sp, sp, #16
	str x20, [sp]
	bl phi_heap_free
	add sp, sp, #16
Lobj_text_moved:
	str x22, [x19]
	str w21, [x19, #12]
	ldp x19, x20, [sp, #16]
	ldp x21, x22, [sp, #32]
	ldp x29, x30, [sp], #48
	ret

	.section __DATA,__data
Lobj_empty:                     ; ('' in the data: writable, as x86's are, so its zero may be written again)
	.byte 0
