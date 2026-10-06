; provides: phi_panic
; requires: phi_console_clear phi_print phi_print_int phi_print_hex
;
; The panic screen, for CPU exceptions: what happened, where, and every
; register, on a red screen and on COM1. Then the CPU stops.
;
; eax = the state saved by the interrupt stub:
;   +0 edi, esi, ebp, esp, ebx, edx, ecx, eax (pushad), +32 vector,
;   +36 error code, +40 eip, +44 cs, +48 eflags
phi_panic:
    cli
    mov ebp, eax
    mov byte [phi_console_color], 0x4F     ; white on red
    call phi_console_clear

    mov esi, .title
    call phi_print

    mov eax, [ebp + 32]
    mov esi, [.names + eax * 4]
    call phi_print
    mov esi, .exception
    call phi_print
    mov eax, [ebp + 32]
    call phi_print_int
    mov esi, .close
    call phi_print

    mov esi, .error_code
    call phi_print
    mov eax, [ebp + 36]
    call phi_print_hex
    mov esi, .at
    call phi_print
    mov eax, [ebp + 40]
    call phi_print_hex
    mov esi, .newline
    call phi_print

    cmp dword [ebp + 32], 14                ; page fault: the address is in cr2
    jne .registers
    mov esi, .address
    call phi_print
    mov eax, cr2
    call phi_print_hex
    mov esi, .newline
    call phi_print

.registers:
    mov esi, .newline
    call phi_print
    xor ebx, ebx
.register:
    mov esi, [.register_names + ebx * 4]
    call phi_print
    mov eax, 7
    sub eax, ebx
    mov eax, [ebp + eax * 4]                ; pushad stored them in reverse order
    call phi_print_hex
    mov esi, .gap
    test ebx, 1
    jz .print_gap
    mov esi, .newline
.print_gap:
    call phi_print
    inc ebx
    cmp ebx, 8
    jb .register

    mov esi, .eflags
    call phi_print
    mov eax, [ebp + 48]
    call phi_print_hex
    mov esi, .newline
    call phi_print

.halt:
    hlt
    jmp .halt

.title:      db 13, 10, ' PHI KERNEL PANIC', 13, 10, 13, 10, ' ', 0
.exception:  db ' (exception ', 0
.close:      db ')', 13, 10, 0
.error_code: db ' error code: ', 0
.at:         db '   at eip: ', 0
.address:    db ' address: ', 0
.eflags:     db ' eflags: ', 0
.gap:        db '   ', 0
.newline:    db 13, 10, 0

; pushad order is eax ecx edx ebx esp ebp esi edi, so index 7 - n is register n here
.register_names: dd .r0, .r1, .r2, .r3, .r4, .r5, .r6, .r7
.r0: db ' eax: ', 0
.r1: db 'ecx: ', 0
.r2: db ' edx: ', 0
.r3: db 'ebx: ', 0
.r4: db ' esp: ', 0
.r5: db 'ebp: ', 0
.r6: db ' esi: ', 0
.r7: db 'edi: ', 0

.names:
    dd .e0, .e1, .e2, .e3, .e4, .e5, .e6, .e7, .e8, .e9, .e10, .e11, .e12, .e13, .e14, .e15
    dd .e16, .e17, .e18, .e19, .e20, .e21, .e15, .e15, .e15, .e15, .e15, .e15, .e15, .e15, .e15, .e15
.e0:  db 'Divide error', 0
.e1:  db 'Debug', 0
.e2:  db 'Non-maskable interrupt', 0
.e3:  db 'Breakpoint', 0
.e4:  db 'Overflow', 0
.e5:  db 'Bound range exceeded', 0
.e6:  db 'Invalid opcode', 0
.e7:  db 'Device not available', 0
.e8:  db 'Double fault', 0
.e9:  db 'Coprocessor segment overrun', 0
.e10: db 'Invalid TSS', 0
.e11: db 'Segment not present', 0
.e12: db 'Stack-segment fault', 0
.e13: db 'General protection fault', 0
.e14: db 'Page fault', 0
.e15: db 'Reserved', 0
.e16: db 'x87 floating-point error', 0
.e17: db 'Alignment check', 0
.e18: db 'Machine check', 0
.e19: db 'SIMD floating-point error', 0
.e20: db 'Virtualization exception', 0
.e21: db 'Control protection exception', 0
