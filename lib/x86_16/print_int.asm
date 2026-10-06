; provides: phi_print_int phi_print_uint
; requires: phi_int_to_str phi_print
;
; Print eax in decimal to the screen and COM1: signed or unsigned.
phi_print_int:
    mov di, phi_print_int_text
    mov cx, 12
    call phi_int_to_str
    mov si, phi_print_int_text
    jmp phi_print

phi_print_uint:
    mov di, phi_print_int_text
    mov cx, 12
    call phi_uint_to_str
    mov si, phi_print_int_text
    jmp phi_print

phi_print_int_text:
    times 12 db 0
