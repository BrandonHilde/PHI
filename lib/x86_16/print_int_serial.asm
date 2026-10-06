; provides: phi_print_int_serial
; requires: phi_int_to_str phi_print_serial
;
; Print eax as signed decimal to COM1 only.
phi_print_int_serial:
    mov di, .text
    mov cx, 12
    call phi_int_to_str
    mov si, .text
    jmp phi_print_serial
.text:
    times 12 db 0
