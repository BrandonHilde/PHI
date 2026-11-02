; provides: phi_print_int phi_print_uint
; requires: phi_int_to_str phi_print
;
; Print eax in decimal to the screen and COM1: signed or unsigned. The digits
; are on this call's own stack, so cores printing at the same time don't share them.
phi_print_int:
    sub esp, 12
    mov edi, esp
    mov ecx, 12
    call phi_int_to_str
    mov esi, esp
    call phi_print
    add esp, 12
    ret

phi_print_uint:
    sub esp, 12
    mov edi, esp
    mov ecx, 12
    call phi_uint_to_str
    mov esi, esp
    call phi_print
    add esp, 12
    ret
