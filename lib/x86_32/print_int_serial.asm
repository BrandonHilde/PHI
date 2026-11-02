; provides: phi_print_int_serial phi_print_uint_serial
; requires: phi_int_to_str phi_print_serial
;
; Print eax in decimal to COM1 only: signed or unsigned. The digits
; are on this call's own stack, so cores printing at the same time don't share them.
phi_print_int_serial:
    sub esp, 12
    mov edi, esp
    mov ecx, 12
    call phi_int_to_str
    mov esi, esp
    call phi_print_serial
    add esp, 12
    ret

phi_print_uint_serial:
    sub esp, 12
    mov edi, esp
    mov ecx, 12
    call phi_uint_to_str
    mov esi, esp
    call phi_print_serial
    add esp, 12
    ret
