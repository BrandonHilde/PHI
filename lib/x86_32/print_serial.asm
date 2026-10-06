; provides: phi_print_serial
; requires: phi_serial_putc
;
; Print the zero-terminated string at esi to COM1 only (the `debug` statement).
phi_print_serial:
    lodsb
    test al, al
    jz .done
    call phi_serial_putc
    jmp phi_print_serial
.done:
    ret
