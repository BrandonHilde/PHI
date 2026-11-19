; provides: phi_thread_key phi_foreign_block
; init: phi_foreign_init
;
; Where outside code comes into a macOS program (a foreign entry: C calling a PHI method)
; it may be on a thread that isn't one of PHI's: a library's own, as one that plays sound
; has. Each thread's block (x28: throw.s's) is kept under a pthread key, so the entry finds
; the thread's own whichever it is; a thread that has none is given one the first time,
; with a spare stack for its faults (faults.s), which it keeps for as long as it lives.
	.section __TEXT,__text,regular,pure_instructions
	.p2align 2

; when the program starts: the key, and the first thread's block (x28) under it
phi_foreign_init:
	stp x29, x30, [sp, #-16]!
	mov x29, sp
	adrp x0, phi_thread_key@PAGE
	add x0, x0, phi_thread_key@PAGEOFF
	mov x1, #0
	bl _pthread_key_create
	adrp x9, phi_thread_key@PAGE
	ldr x0, [x9, phi_thread_key@PAGEOFF]
	mov x1, x28
	bl _pthread_setspecific
	ldp x29, x30, [sp], #16
	ret

; -> x28 = the running thread's block, made if it has none. Called as C calls (it keeps
; x19-x27; the entry has put C's values away before it).
phi_foreign_block:
	stp x29, x30, [sp, #-32]!
	mov x29, sp
	str x19, [sp, #16]
	adrp x9, phi_thread_key@PAGE
	ldr x0, [x9, phi_thread_key@PAGEOFF]
	bl _pthread_getspecific
	cbnz x0, Lforeign_have
	; mmap(0, 16 KB + 64 KB, read and write, private and not a file's): its block at the
	; start, then its spare stack
	mov x0, #0
	mov x1, #0x4000
	movk x1, #1, lsl #16    ; 81920
	mov w2, #3
	mov w3, #0x1002
	mov w4, #-1
	mov x5, #0
	bl _mmap
	cmn x0, #1
	b.eq Lforeign_none
	mov x19, x0
	str x19, [x19]              ; (its own address; mmap's memory is zeros: no catch, nothing to give back)
	add x9, x19, #16, lsl #12
	sub sp, sp, #32             ; sigaltstack(&{ ss_sp, ss_size, ss_flags }, 0)
	str x9, [sp]
	mov x10, #65536
	str x10, [sp, #8]
	str wzr, [sp, #16]
	mov x0, sp
	mov x1, #0
	bl _sigaltstack
	add sp, sp, #32
	add x9, x19, #20, lsl #12   ; the top of its spare stack
	str x9, [x19, #1016]
	adrp x9, phi_thread_key@PAGE
	ldr x0, [x9, phi_thread_key@PAGEOFF]
	mov x1, x19
	bl _pthread_setspecific
	mov x0, x19
Lforeign_have:
	mov x28, x0
	ldr x19, [sp, #16]
	ldp x29, x30, [sp], #32
	ret
Lforeign_none:
	bl _abort

	.section __DATA,__data
	.p2align 3
phi_thread_key: .quad 0
