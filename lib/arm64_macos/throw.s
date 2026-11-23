; provides: phi_throw phi_fault_entry phi_line_at phi_index_error phi_text_error phi_main_thread phi_stack_restore
; requires: phi_print phi_exit phi_log_take phi_log_back
;
; Errors in macOS programs, as x86_64_linux/throw.asm has them: w0 = the line, x1 = the
; file's name (0 if not known), x2 = the text, w3 = the kind (ErrorKind in
; lib/phi/lang/error.phi). The error goes into the running thread's block (x28: every
; thread has one, the first this file's phi_main_thread), laid out as an Error is (kind,
; line, file, text, message); then a catch carries on, or CR LF, "PHI: " and the message
; are printed and the program ends with -1.
;
; A thread's block: +0 itself, +8 the innermost catch (or 0), +16 the innermost running
; method with things to give back (or 0), +24 the error being thrown, +1016 the top of its
; spare stack. A catch's record, 8 bytes a field: +0 the catch before it, +8 sp, +16 x29,
; +24 the unwind list when it was set, +32 where to go, +40 how deep the thread was in the
; log's lock. A method's: +0 the record before it, +8 what gives back its things (called
; with x29 its frame), +16 its x29.
	.section __TEXT,__text,regular,pure_instructions
	.p2align 2

; It never comes back: everything but x28 is its own (a catch carries on with nothing in
; a register, from a method that kept every register its caller needs).
phi_throw:
	add x19, x28, #24           ; this thread's error
	str w3, [x19]               ; its kind
	str w0, [x19, #4]           ; its line
	mov x9, x2                  ; the text (the copies change x0-x2, x16 and x17 only)
	add x0, x19, #8
	mov w2, #64
	bl Lthrow_copy              ; the file's name
	add x0, x19, #73
	mov x1, x9
	mov w2, #255
	bl Lthrow_copy              ; the text

	; the message: the text, " on line ", the line, then " in " and the file if it's known
	add x0, x19, #329
	mov w2, #383
	add x1, x19, #73
	bl Lthrow_append
	ldr w10, [x19, #4]
	cbz w10, Lthrow_said        ; (a line that isn't known: the text alone)
	adrp x1, Lthrow_on_line@PAGE
	add x1, x1, Lthrow_on_line@PAGEOFF
	bl Lthrow_append
	; the line in decimal, backwards into 16 bytes below sp, then appended
	sub sp, sp, #16
	add x1, sp, #15
	strb wzr, [x1]
	mov w11, #10
Lthrow_digit:
	udiv w12, w10, w11
	msub w13, w12, w11, w10
	add w13, w13, #48
	strb w13, [x1, #-1]!
	mov w10, w12
	cbnz w10, Lthrow_digit
	bl Lthrow_append
	add sp, sp, #16
	ldrb w10, [x19, #8]
	cbz w10, Lthrow_said
	adrp x1, Lthrow_in@PAGE
	add x1, x1, Lthrow_in@PAGEOFF
	bl Lthrow_append
	add x1, x19, #8
	bl Lthrow_append
Lthrow_said:
	strb wzr, [x0]

	ldr x20, [x28, #8]          ; the innermost catch
	cbz x20, Lthrow_uncaught
Lthrow_unwind:
	; each method that's still running above the catch gives back its things
	ldr x21, [x28, #16]
	ldr x9, [x20, #24]
	cmp x21, x9
	b.eq Lthrow_resume
	ldr x9, [x21]
	str x9, [x28, #16]          ; off the list first
	ldr x9, [x21, #8]
	ldr x29, [x21, #16]
	blr x9
	b Lthrow_unwind
Lthrow_resume:
	ldr w9, [x19]
	cmp w9, #8                  ; ErrorKind.STACK: still on its spare stack
	b.ne Lthrow_stack_fine
	bl phi_stack_restore
Lthrow_stack_fine:
	ldr x0, [x20, #40]
	bl phi_log_back
	ldr x9, [x20]
	str x9, [x28, #8]           ; this catch is done
	ldr x9, [x20, #8]
	mov sp, x9
	ldr x29, [x20, #16]
	ldr x9, [x20, #32]
	br x9

Lthrow_uncaught:
	bl phi_log_take             ; (printed whole, even with other threads logging)
	adrp x0, Lthrow_prefix@PAGE
	add x0, x0, Lthrow_prefix@PAGEOFF
	bl phi_print
	add x0, x28, #24 + 329
	bl phi_print
	adrp x0, Lthrow_newline@PAGE
	add x0, x0, Lthrow_newline@PAGEOFF
	bl phi_print
	movn w0, #0
	b phi_exit

; x1's text (none if x1 is 0) at x0, up to w2 characters, then a zero
Lthrow_copy:
	mov x16, x30
	bl Lthrow_append
	strb wzr, [x0]
	ret x16

; x1's text (none if x1 is 0) at x0, up to w2 characters (w2 counts down, x0 moves on)
Lthrow_append:
	cbz x1, Lthrow_appended
Lthrow_next:
	cbz w2, Lthrow_appended
	ldrb w17, [x1], #1
	cbz w17, Lthrow_appended
	strb w17, [x0], #1
	sub w2, w2, #1
	b Lthrow_next
Lthrow_appended:
	ret

; after a throw of the stack running out, on its way back to the catch: nothing to do, as
; what's past the end of the stack stays there to be stopped by
phi_stack_restore:
	ret

; ---- CPU exceptions, and the checks the code makes in their place, thrown as errors:
; w0 = the kind (ErrorKind: 3 division by zero, 4 empty, 5 access, 8 stack), x1 = where it
; happened (its line and file are looked up)
phi_fault_entry:
	mov w3, w0
	adrp x2, Lthrow_refused@PAGE
	add x2, x2, Lthrow_refused@PAGEOFF
	cmp w3, #3
	b.ne 1f
	adrp x2, Lthrow_divided@PAGE
	add x2, x2, Lthrow_divided@PAGEOFF
1:	cmp w3, #4
	b.ne 2f
	adrp x2, Lthrow_empty@PAGE
	add x2, x2, Lthrow_empty@PAGEOFF
2:	cmp w3, #8
	b.ne 3f
	adrp x2, Lthrow_stack@PAGE
	add x2, x2, Lthrow_stack@PAGEOFF
3:	mov x0, x1
	mov x19, x2
	mov w20, w3
	bl phi_line_at
	mov x2, x19
	mov w3, w20
	b phi_throw

; x0 = an address in the program's code -> w0 = the line it's on, x1 = its file's name; 0 and
; 0 when it isn't PHI code (the runtime) or isn't known. phi_lines is a count, then a table for
; the program and each compiled library: where its code starts, its files' names, how many
; marks, then each mark in 8 bytes, its offset in that code (4) and its file (top 12 bits) and
; line (20 bits), the last of line 0 where the code ends (A64Generator.EmitLineTable). The
; mark nearest at or before the address, in any table, is its line. Changes x9-x17 only (its
; one caller, phi_fault_entry, is on its way to phi_throw, and keeps what it needs in x19, x20).
phi_line_at:
	adrp x9, phi_lines@PAGE
	add x9, x9, phi_lines@PAGEOFF
	ldr x10, [x9], #8           ; tables
	mov x15, #-1                ; how far before the address the nearest mark so far is
	mov x16, #0                 ; its table
	mov w17, #0                 ; its file and line
Lthrow_line_table:
	cbz x10, Lthrow_line_found
	ldr x11, [x9], #8           ; a table
	sub x10, x10, #1
	ldr x12, [x11]              ; where its code starts
	subs x12, x0, x12           ; the address, in its code
	b.lo Lthrow_line_table
	lsr x13, x12, #32
	cbnz x13, Lthrow_line_table ; (4 GB on: not its)
	ldr w13, [x11, #16]         ; its marks
	add x14, x11, #24
Lthrow_line_mark:
	cbz w13, Lthrow_line_table
	ldr w1, [x14]               ; its offset (x1 is free until the answer)
	cmp x1, x12
	b.hi Lthrow_line_table      ; past the address: the one before was this table's last
	sub x1, x12, x1
	cmp x1, x15
	b.hs Lthrow_line_skip
	mov x15, x1
	mov x16, x11
	ldr w17, [x14, #4]
Lthrow_line_skip:
	add x14, x14, #8
	sub w13, w13, #1
	b Lthrow_line_mark
Lthrow_line_found:
	and w0, w17, #0xFFFFF
	mov x1, #0
	cbz w0, Lthrow_line_done
	lsr w17, w17, #20
	ldr x1, [x16, #8]           ; its files' names
	ldr x1, [x1, x17, lsl #3]
Lthrow_line_done:
	ret

; An index was outside its array: w0 = the line, x1 = the file's name
phi_index_error:
	adrp x2, Lthrow_index@PAGE
	add x2, x2, Lthrow_index@PAGEOFF
	mov w3, #1                  ; ErrorKind.INDEX
	b phi_throw

; Text didn't fit in the str it was going to, which the program didn't ask to have cut
phi_text_error:
	adrp x2, Lthrow_text@PAGE
	add x2, x2, Lthrow_text@PAGEOFF
	mov w3, #9                  ; ErrorKind.TEXT
	b phi_throw

	.section __TEXT,__const
Lthrow_prefix:
	.byte 13, 10, 80, 72, 73, 58, 32, 0          ; CR LF "PHI: "
Lthrow_on_line:
	.asciz " on line "
Lthrow_in:
	.asciz " in "
Lthrow_newline:
	.byte 13, 10, 0
Lthrow_divided:
	.asciz "division by zero"
Lthrow_empty:
	.asciz "an empty object or a null pointer was used"
Lthrow_stack:
	.asciz "the stack ran out: calls too deep"
Lthrow_refused:
	.asciz "memory that is not there was used, or the CPU refused an instruction"
Lthrow_index:
	.asciz "index out of range"
Lthrow_text:
	.asciz "text doesn't fit in its str"

	.section __DATA,__data
	.p2align 4
; the first thread's block (x28 in _main); its spare stack is faults.s's
phi_main_thread:
	.space 1024
