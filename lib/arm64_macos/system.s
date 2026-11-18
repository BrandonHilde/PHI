; provides: phi_exit phi_argc phi_argv phi_read_byte OS_ReadKey Bootloader_WaitForKeyPress phi_read_char OS_KeyAvailable OS_CurrentTask OS_Arguments OS_Grow OS_FreeMemory OS_TotalMemory OS_ClearScreen phi_read_line
; requires: phi_flush phi_write_all
; init: phi_macos_start
;
; What a program asks of the system, for macOS programs, as x86_64_linux's system.asm has
; it: answered by libSystem (macOS has no system calls a program may make itself; their
; numbers change from release to release).
;
; The OS_ routines are built-ins a PHI method calls: their arguments are on the stack as
; PHI's methods give them, the last at [sp], 8 bytes each, and what they give back is in
; x0. Like every routine here they keep x19-x29 (and x28, the thread's block, never changes).
	.section __TEXT,__text,regular,pure_instructions
	.p2align 2

; x0 = argc, x1 = argv, from _main: kept, and the arguments joined by spaces for OS.Arguments
phi_macos_start:
	adrp x16, phi_argc@PAGE
	add x16, x16, phi_argc@PAGEOFF
	str x0, [x16]
	adrp x16, phi_argv@PAGE
	add x16, x16, phi_argv@PAGEOFF
	str x1, [x16]
	adrp x9, phi_arguments@PAGE
	add x9, x9, phi_arguments@PAGEOFF
	add x10, x9, #1023          ; the last place (for the zero)
	add x11, x1, #8             ; past the program's own name
	subs w12, w0, #1
	b.le Lsystem_args_done
Lsystem_args_next:
	ldr x13, [x11], #8
Lsystem_args_copy:
	ldrb w14, [x13], #1
	cbz w14, Lsystem_args_end
	cmp x9, x10
	b.hs Lsystem_args_done
	strb w14, [x9], #1
	b Lsystem_args_copy
Lsystem_args_end:
	subs w12, w12, #1
	b.eq Lsystem_args_done
	cmp x9, x10
	b.hs Lsystem_args_done
	mov w14, #32
	strb w14, [x9], #1
	b Lsystem_args_next
Lsystem_args_done:
	strb wzr, [x9]
	ret

; w0 = the exit code: what's logged written out, then the program ends
phi_exit:
	mov w19, w0
	bl phi_flush
	mov w0, w19
	bl _exit

; -> w0 = the next byte of standard input, or -1 at its end
phi_read_byte:
	stp x29, x30, [sp, #-16]!
	mov x29, sp
	mov w0, #0
	adrp x1, phi_byte@PAGE
	add x1, x1, phi_byte@PAGEOFF
	mov x2, #1
	bl _read
	cmp x0, #1
	b.ne Lsystem_byte_end
	adrp x1, phi_byte@PAGE
	ldrb w0, [x1, phi_byte@PAGEOFF]
	ldp x29, x30, [sp], #16
	ret
Lsystem_byte_end:
	mov w0, #-1
	ldp x29, x30, [sp], #16
	ret

; -> w0 = the next character typed; Enter is 13, as on PHI OS (a line ending in 10, or in 13
; 10, gives one), and 0 means the input ended. A terminal hands a program its input a line
; at a time, so this waits for Enter.
OS_ReadKey:
Bootloader_WaitForKeyPress:
phi_read_char:
	stp x29, x30, [sp, #-16]!
	mov x29, sp
	bl phi_flush
Lsystem_key_again:
	bl phi_read_byte
	adrp x9, phi_last_byte@PAGE
	add x9, x9, phi_last_byte@PAGEOFF
	ldrb w10, [x9]
	strb w0, [x9]
	cmp w0, #10
	b.ne Lsystem_key_not_enter
	cmp w10, #13
	b.eq Lsystem_key_again      ; the end of 13 10: that Enter was given already
	mov w0, #13
Lsystem_key_not_enter:
	cmp w0, #0
	csel w0, wzr, w0, lt
	ldp x29, x30, [sp], #16
	ret

; -> w0 = 1 if there's input waiting
OS_KeyAvailable:
	stp x29, x30, [sp, #-32]!
	mov x29, sp
	str wzr, [sp, #16]          ; a pollfd: standard input, anything to read?
	mov w9, #1                  ; POLLIN
	strh w9, [sp, #20]
	strh wzr, [sp, #22]
	add x0, sp, #16
	mov w1, #1
	mov w2, #0                  ; don't wait
	bl _poll
	cmp w0, #1
	cset w0, eq
	ldp x29, x30, [sp], #32
	ret

; -> this program's process number
OS_CurrentTask:
	b _getpid

; -> what came after the program's name on its command line
OS_Arguments:
	adrp x0, phi_arguments@PAGE
	add x0, x0, phi_arguments@PAGEOFF
	ret

; [sp] = bytes -> x0 = where the new memory starts (right after what the heap has), or 0.
; The heap is a reservation of address space made once (no memory in it, and none can be
; reached), grown by making the next pages of it memory. It grows by 4 KB pages, as the
; heap (memory.program_heap) and Linux's brk have it; macOS makes memory 16 KB at a time, so
; what's past the heap's end up to the next 16 KB is memory already.
OS_Grow:
	ldr w9, [sp]
	stp x29, x30, [sp, #-48]!
	mov x29, sp
	stp x19, x20, [sp, #16]
	add x19, x9, #4095          ; the bytes, rounded up to 4 KB pages
	and x19, x19, #~4095
	adrp x20, phi_heap_start@PAGE
	add x20, x20, phi_heap_start@PAGEOFF
	ldr x9, [x20]
	cbnz x9, Lsystem_grow
	; mmap(0, the most, PROT_NONE, MAP_PRIVATE | MAP_ANON, -1, 0)
	mov x0, #0
	mov x1, #0x400000000        ; the most: 16 GB of address space
	mov w2, #0
	mov w3, #0x1002
	mov w4, #-1
	mov x5, #0
	bl _mmap
	cmn x0, #1
	b.eq Lsystem_grow_failed
	str x0, [x20]
	str x0, [x20, #8]
	str x0, [x20, #16]
Lsystem_grow:
	ldp x9, x10, [x20]          ; where it starts, where the heap ends now
	ldr x11, [x20, #16]         ; where its memory ends
	add x12, x10, x19           ; the new end
	sub x13, x12, x9
	mov x14, #0x400000000
	cmp x13, x14
	b.hi Lsystem_grow_failed    ; (past the reservation: there's no more)
	cmp x12, x11
	b.ls Lsystem_grown          ; (memory already)
	; mprotect(where its memory ends, up to the new end in 16 KB pages, PROT_READ | PROT_WRITE)
	add x12, x12, #3, lsl #12
	add x12, x12, #0xFFF        ; (16 KB - 1)
	and x12, x12, #~0x3FFF
	mov x0, x11
	sub x1, x12, x11
	mov w2, #3
	str x12, [sp, #32]
	bl _mprotect
	cbnz w0, Lsystem_grow_failed
	ldr x12, [sp, #32]
	str x12, [x20, #16]
Lsystem_grown:
	ldr x0, [x20, #8]
	add x9, x0, x19
	str x9, [x20, #8]
	b Lsystem_grow_done
Lsystem_grow_failed:
	mov x0, #0
Lsystem_grow_done:
	ldp x19, x20, [sp, #16]
	ldp x29, x30, [sp], #48
	ret

; -> all memory, in KB (hw.memsize)
OS_TotalMemory:
	stp x29, x30, [sp, #-32]!
	mov x29, sp
	adrp x0, Lsystem_memsize@PAGE
	add x0, x0, Lsystem_memsize@PAGEOFF
	add x1, sp, #16
	mov x9, #8
	str x9, [sp, #24]
	add x2, sp, #24
	mov x3, #0
	mov x4, #0
	bl _sysctlbyname
	ldr x0, [sp, #16]
	lsr x0, x0, #10
	ldp x29, x30, [sp], #32
	ret

; -> free memory, in KB (the free pages the kernel counts: host_statistics64's HOST_VM_INFO64)
OS_FreeMemory:
	stp x29, x30, [sp, #-208]!
	mov x29, sp
	bl _mach_host_self
	mov w1, #4                  ; HOST_VM_INFO64
	add x2, sp, #24             ; a vm_statistics64 (free_count first)
	mov w9, #38                 ; HOST_VM_INFO64_COUNT
	str w9, [sp, #16]
	add x3, sp, #16
	bl _host_statistics64
	mov x0, #0
	cbnz w0, Lsystem_free_done
	ldr w0, [sp, #24]
	adrp x9, _vm_page_size@GOTPAGE
	ldr x9, [x9, _vm_page_size@GOTPAGEOFF]
	ldr x9, [x9]
	mul x0, x0, x9
	lsr x0, x0, #10
Lsystem_free_done:
	ldp x29, x30, [sp], #208
	ret

; clear the terminal and put the cursor at its top left
OS_ClearScreen:
	stp x29, x30, [sp, #-16]!
	mov x29, sp
	bl phi_flush
	mov w0, #1
	adrp x1, Lsystem_clear@PAGE
	add x1, x1, Lsystem_clear@PAGEOFF
	mov w2, #7
	bl phi_write_all
	ldp x29, x30, [sp], #16
	ret

; x0 = where a line of standard input goes (the `ask` statement), w1 = the most characters to
; keep; the rest of a longer line is skipped. A line ends at a newline (a carriage return
; before it is dropped) or at the end of the input.
phi_read_line:
	stp x29, x30, [sp, #-48]!
	mov x29, sp
	stp x19, x20, [sp, #16]
	str x21, [sp, #32]
	mov x19, x0
	mov w20, w1
	mov w21, #0                 ; characters kept
	bl phi_flush                ; anything printed before the prompt
Lsystem_line_next:
	bl phi_read_byte
	tbnz w0, #31, Lsystem_line_done
	cmp w0, #10
	b.eq Lsystem_line_done
	cmp w0, #13
	b.eq Lsystem_line_next
	cmp w21, w20
	b.hs Lsystem_line_next
	strb w0, [x19, w21, uxtw]
	add w21, w21, #1
	b Lsystem_line_next
Lsystem_line_done:
	strb wzr, [x19, w21, uxtw]
	ldp x19, x20, [sp, #16]
	ldr x21, [sp, #32]
	ldp x29, x30, [sp], #48
	ret

	.section __TEXT,__const
Lsystem_clear:
	.byte 27, 91, 50, 74, 27, 91, 72            ; ESC [2J ESC [H
Lsystem_memsize:
	.asciz "hw.memsize"

	.section __DATA,__data
	.p2align 3
phi_argc:
	.quad 0
phi_argv:
	.quad 0
phi_heap_start:
	.quad 0, 0, 0               ; where the heap's reservation starts, where the heap ends, where its memory ends
phi_byte:
	.byte 0
phi_last_byte:
	.byte 0
	.zerofill __DATA,__bss,phi_arguments,1024,3
