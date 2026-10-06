; provides: phi_print_hex
; requires: phi_console_putc
;
; Print eax as 0x followed by 8 hex digits (screen and COM1). Preserves registers.
phi_print_hex:
    pushad
    mov edx, eax
    mov al, '0'
    call phi_console_putc
    mov al, 'x'
    call phi_console_putc
    mov ecx, 8
.digit:
    rol edx, 4
    mov eax, edx
    and eax, 0x0F
    mov al, [.digits + eax]
    call phi_console_putc
    loop .digit
    popad
    ret
.digits: db '0123456789ABCDEF'
