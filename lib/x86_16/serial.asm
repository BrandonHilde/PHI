; provides: phi_serial_putc
;
; Write al to COM1. Waits until the port can take another byte.
; Preserves all registers.
phi_serial_putc:
    push dx
    push ax
    mov dx, 0x3FD           ; line status register
.wait:
    in al, dx
    test al, 0x20           ; transmitter ready?
    jz .wait
    pop ax
    mov dx, 0x3F8
    out dx, al
    pop dx
    ret
