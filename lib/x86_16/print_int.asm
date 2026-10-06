; provides: phi_print_int
; requires: phi_int_to_str phi_print
;
; Print eax as signed decimal to the screen and COM1.
phi_print_int:
    mov di, .text
    mov cx, 12
    call phi_int_to_str
    mov si, .text
    jmp phi_print
.text:
    times 12 db 0
