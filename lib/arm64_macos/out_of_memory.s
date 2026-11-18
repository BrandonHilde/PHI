; provides: phi_out_of_memory phi_object_line
; requires: phi_throw
;
; The heap had no room for a new object, as x86_64_linux's out_of_memory.asm: phi_object_line
; is the line of the statement that asked (the code sets it as it makes an object). One that
; isn't caught prints a message and ends the program with -1.
	.section __TEXT,__text,regular,pure_instructions
	.p2align 2
phi_out_of_memory:
	adrp x9, phi_object_line@PAGE
	ldr w0, [x9, phi_object_line@PAGEOFF]
	mov x1, #0                  ; the file isn't known
	adrp x2, Lmemory_text@PAGE
	add x2, x2, Lmemory_text@PAGEOFF
	mov w3, #2                  ; ErrorKind.MEMORY
	b phi_throw

	.section __TEXT,__const
Lmemory_text:
	.asciz "out of memory for a new object"

	.section __DATA,__data
	.p2align 2
phi_object_line:
	.long 0
