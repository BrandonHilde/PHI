; provides: phi_print
; requires: phi_serial_putc
;
; Print the zero-terminated string at si to the screen (BIOS teletype) and COM1.
phi_print:
    lodsb
    test al, al
    jz .done
    push ax
    mov ah, 0x0E
    mov bx, 0x000F          ; page 0, white (used in graphics modes)
    int 0x10
    pop ax                  ; the BIOS may change al
    call phi_serial_putc
    jmp phi_print
.done:
    ret
