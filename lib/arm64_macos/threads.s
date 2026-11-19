; provides: OS_ThreadEntry OS_ThreadCreate phi_thread_main
; requires: phi_throw phi_thread_key
;
; A macOS program's threads (host/threads_macos.phi starts them with pthread_create): each
; runs on a stack pthread makes it of the size it was started with; the top of the memory
; the program gave it holds its block (x28: throw.s's) and what it's to run (host/threads.phi's
; ThreadFrame: its method, then its value). Its spare stack, where a fault is handled (the
; stack running out has no room for that), is 64 KB of its own: the block says where its top
; is (+1016), for faults.s.
	.section __TEXT,__text,regular,pure_instructions
	.p2align 2

; -> where a new thread starts, for pthread_create
OS_ThreadEntry:
	adrp x0, phi_thread_main@PAGE
	add x0, x0, phi_thread_main@PAGEOFF
	ret

; OS.ThreadCreate (a built-in's arguments: the first at [sp], so [sp] = its ThreadFrame,
; [sp + 8] = its stack's size, [sp + 16] = where its pthread goes): a pthread with a stack of that size (rounded up to pages),
; starting at phi_thread_main -> w0 = 0 if it started, else pthread_create's error
OS_ThreadCreate:
	ldr x9, [sp]
	ldr x10, [sp, #8]
	ldr x11, [sp, #16]
	stp x29, x30, [sp, #-112]!
	mov x29, sp
	stp x19, x20, [sp, #16]
	str x21, [sp, #32]
	mov x19, x9
	add x20, x10, #3, lsl #12
	add x20, x20, #0xFFF
	and x20, x20, #~0x3FFF      ; (16 KB pages)
	mov x21, x11
	add x0, sp, #48             ; a pthread_attr_t: [sp + 48, sp + 112)
	bl _pthread_attr_init
	add x0, sp, #48
	mov x1, x20
	bl _pthread_attr_setstacksize
	mov x0, x21
	add x1, sp, #48
	adrp x2, phi_thread_main@PAGE
	add x2, x2, phi_thread_main@PAGEOFF
	mov x3, x19
	bl _pthread_create
	mov w20, w0
	add x0, sp, #48
	bl _pthread_attr_destroy
	mov w0, w20
	ldp x19, x20, [sp, #16]
	ldr x21, [sp, #32]
	ldp x29, x30, [sp], #112
	ret

; x0 = its ThreadFrame (pthread's start routine): its method is called with its value, then
; the frame's method is made 0, which says it has finished
phi_thread_main:
	stp x29, x30, [sp, #-96]!
	mov x29, sp
	stp x19, x20, [sp, #16]
	stp x21, x22, [sp, #32]
	stp x23, x24, [sp, #48]
	stp x25, x26, [sp, #64]
	stp x27, x28, [sp, #80]
	mov x19, x0
	add x28, x19, #16           ; its block, just above the frame
	str x28, [x28]              ; (its own address)
	str xzr, [x28, #8]          ; no catch yet
	str xzr, [x28, #16]         ; nothing to give back yet
	str xzr, [x28, #1016]
	adrp x9, phi_thread_key@PAGE    ; (where a foreign entry finds it: foreign.s)
	ldr x0, [x9, phi_thread_key@PAGEOFF]
	mov x1, x28
	bl _pthread_setspecific
	; its spare stack: mmap(0, 64 KB, read and write, private and not a file's)
	mov x0, #0
	mov x1, #65536
	mov w2, #3
	mov w3, #0x1002
	mov w4, #-1
	mov x5, #0
	bl _mmap
	mov x20, #0
	cmn x0, #1
	b.eq Lthreads_run
	mov x20, x0
	sub sp, sp, #32             ; sigaltstack(&{ ss_sp, ss_size, ss_flags }, 0)
	str x20, [sp]
	mov x9, #65536
	str x9, [sp, #8]
	str wzr, [sp, #16]
	mov x0, sp
	mov x1, #0
	bl _sigaltstack
	add sp, sp, #32
	add x9, x20, #16, lsl #12
	str x9, [x28, #1016]
Lthreads_run:
	sub sp, sp, #16             ; (PHI's call: its value at [sp])
	ldr x9, [x19, #8]
	str x9, [sp]
	ldr x9, [x19]
	blr x9
	add sp, sp, #16
	cbz x20, Lthreads_done
	sub sp, sp, #32             ; the spare stack no longer: SS_DISABLE, then it's given back
	str xzr, [sp]
	str xzr, [sp, #8]
	mov w9, #4
	str w9, [sp, #16]
	mov x0, sp
	mov x1, #0
	bl _sigaltstack
	add sp, sp, #32
	mov x0, x20
	mov x1, #65536
	bl _munmap
Lthreads_done:
	stlr xzr, [x19]             ; finished (what was its stack may be freed from now on)
	mov x0, #0
	ldp x19, x20, [sp, #16]
	ldp x21, x22, [sp, #32]
	ldp x23, x24, [sp, #48]
	ldp x25, x26, [sp, #64]
	ldp x27, x28, [sp, #80]
	ldp x29, x30, [sp], #96
	ret
