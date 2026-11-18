; provides: _phi_msg_point _phi_msg_rect _phi_window_delegate _phi_close_asked
;
; What a macOS program's window (host/window_macos.phi) needs that PHI can't say itself,
; as functions PHI calls the way C calls them (AAPCS64: x0 up, a result in x0):
;
;   _phi_msg_point: [object selector], which gives an NSPoint (two doubles, in d0 and d1),
;                   into the two at x2
;   _phi_msg_rect:  the same for an NSRect (four doubles, in d0-d3), into the four at x2
;   _phi_window_delegate: an object of a class made here (PhiWindowDelegate, an NSObject)
;                   whose windowShouldClose: says NO and keeps which window asked, so closing
;                   a window is the program's to do, as a CLOSE event
;   _phi_close_asked: the window that last asked to close (then none), or 0
	.section __TEXT,__text,regular,pure_instructions
	.p2align 2

_phi_msg_point:
	stp x29, x30, [sp, #-32]!
	mov x29, sp
	str x19, [sp, #16]
	mov x19, x2
	bl _objc_msgSend
	stp d0, d1, [x19]
	ldr x19, [sp, #16]
	ldp x29, x30, [sp], #32
	ret

_phi_msg_rect:
	stp x29, x30, [sp, #-32]!
	mov x29, sp
	str x19, [sp, #16]
	mov x19, x2
	bl _objc_msgSend
	stp d0, d1, [x19]
	stp d2, d3, [x19, #16]
	ldr x19, [sp, #16]
	ldp x29, x30, [sp], #32
	ret

; windowShouldClose: (self, _cmd, window) -> NO, with window kept for _phi_close_asked
Lappkit_should_close:
	adrp x9, Lappkit_asked@PAGE
	str x2, [x9, Lappkit_asked@PAGEOFF]
	mov w0, #0
	ret

_phi_close_asked:
	adrp x9, Lappkit_asked@PAGE
	add x9, x9, Lappkit_asked@PAGEOFF
	ldr x0, [x9]
	str xzr, [x9]
	ret

_phi_window_delegate:
	stp x29, x30, [sp, #-32]!
	mov x29, sp
	stp x19, x20, [sp, #16]
	adrp x9, Lappkit_delegate@PAGE
	ldr x0, [x9, Lappkit_delegate@PAGEOFF]
	cbnz x0, Lappkit_made
	adrp x0, Lappkit_nsobject@PAGE
	add x0, x0, Lappkit_nsobject@PAGEOFF
	bl _objc_getClass
	adrp x1, Lappkit_class_name@PAGE
	add x1, x1, Lappkit_class_name@PAGEOFF
	mov x2, #0
	bl _objc_allocateClassPair
	mov x19, x0
	adrp x0, Lappkit_should_name@PAGE
	add x0, x0, Lappkit_should_name@PAGEOFF
	bl _sel_registerName
	mov x1, x0
	mov x0, x19
	adrp x2, Lappkit_should_close@PAGE
	add x2, x2, Lappkit_should_close@PAGEOFF
	adrp x3, Lappkit_should_types@PAGE
	add x3, x3, Lappkit_should_types@PAGEOFF
	bl _class_addMethod
	mov x0, x19
	bl _objc_registerClassPair
	; [[PhiWindowDelegate alloc] init]
	adrp x0, Lappkit_alloc@PAGE
	add x0, x0, Lappkit_alloc@PAGEOFF
	bl _sel_registerName
	mov x1, x0
	mov x0, x19
	bl _objc_msgSend
	mov x20, x0
	adrp x0, Lappkit_init@PAGE
	add x0, x0, Lappkit_init@PAGEOFF
	bl _sel_registerName
	mov x1, x0
	mov x0, x20
	bl _objc_msgSend
	adrp x9, Lappkit_delegate@PAGE
	str x0, [x9, Lappkit_delegate@PAGEOFF]
Lappkit_made:
	ldp x19, x20, [sp, #16]
	ldp x29, x30, [sp], #32
	ret

	.section __TEXT,__cstring,cstring_literals
Lappkit_nsobject:
	.asciz "NSObject"
Lappkit_class_name:
	.asciz "PhiWindowDelegate"
Lappkit_should_name:
	.asciz "windowShouldClose:"
Lappkit_should_types:
	.asciz "c@:@"
Lappkit_alloc:
	.asciz "alloc"
Lappkit_init:
	.asciz "init"

	.section __DATA,__data
	.p2align 3
Lappkit_delegate:
	.quad 0
Lappkit_asked:
	.quad 0
