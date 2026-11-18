; provides: phi_log_take phi_log_give phi_log_held phi_log_back
;
; A whole log statement at a time, across a program's threads, as x86_64_linux's
; log_lock.asm: the thread that has the lock is told by its block's address (x28), so a log
; inside a log doesn't wait for itself; and a throw back to a catch puts the lock as it was
; at the catch (phi_log_held, phi_log_back). Changes x9-x11 (and x0).
	.section __TEXT,__text,regular,pure_instructions
	.p2align 2

phi_log_take:
	adrp x9, phi_log_owner@PAGE
	add x9, x9, phi_log_owner@PAGEOFF
	ldr x10, [x9]
	cmp x10, x28
	b.eq Llog_nested
Llog_try:
	mov x10, #0
	casa x10, x28, [x9]         ; (acquire: what the last thread logged is seen)
	cbz x10, Llog_nested
Llog_wait:
	yield                       ; another thread is logging
	ldr x10, [x9]
	cbnz x10, Llog_wait
	b Llog_try
Llog_nested:
	ldr w10, [x9, #8]           ; (only the thread that has it changes this)
	add w10, w10, #1
	str w10, [x9, #8]
	ret

phi_log_give:
	adrp x9, phi_log_owner@PAGE
	add x9, x9, phi_log_owner@PAGEOFF
	ldr w10, [x9, #8]
	cbz w10, Llog_given
	subs w10, w10, #1
	str w10, [x9, #8]
	b.ne Llog_given             ; a log inside a log: the outer one gives it back
	stlr xzr, [x9]
Llog_given:
	ret

; -> x0 = how many logs deep this thread is in the lock (0 if it hasn't got it)
phi_log_held:
	adrp x9, phi_log_owner@PAGE
	add x9, x9, phi_log_owner@PAGEOFF
	ldr x10, [x9]
	mov x0, #0
	cmp x10, x28
	b.ne Llog_held
	ldr w0, [x9, #8]
Llog_held:
	ret

; x0 = how deep this thread was when a catch was set: the throw coming back to it gives back
; the logs it left
phi_log_back:
	adrp x9, phi_log_owner@PAGE
	add x9, x9, phi_log_owner@PAGEOFF
	ldr x10, [x9]
	cmp x10, x28
	b.ne Llog_back
	str w0, [x9, #8]
	cbnz w0, Llog_back
	stlr xzr, [x9]
Llog_back:
	ret

	.section __DATA,__data
	.p2align 6
phi_log_owner:
	.quad 0
phi_log_depth:
	.long 0
