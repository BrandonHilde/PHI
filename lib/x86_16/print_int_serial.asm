; provides: phi_print_int_serial phi_print_uint_serial
; requires: phi_int_to_str phi_print_serial
;
; Print eax in decimal to COM1 only: signed or unsigned.
phi_print_int_serial:
    mov di, phi_print_int_serial_text
    mov cx, 12
    call phi_int_to_str
    mov si, phi_print_int_serial_text
    jmp phi_print_serial

phi_print_uint_serial:
    mov di, phi_print_int_serial_text
    mov cx, 12
    call phi_uint_to_str
    mov si, phi_print_int_serial_text
    jmp phi_print_serial

phi_print_int_serial_text:
    times 12 db 0
