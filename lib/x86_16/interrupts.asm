; provides: OS_SetInterruptHandler OS_EndOfInterrupt OS_EnableInterrupts OS_DisableInterrupts OS_UnmaskIrq OS_MaskIrq
;
; Real-mode interrupts: the vector table is at 0000:0000, four bytes per vector.
; In the BIOS's setup, IRQ 0-7 are vectors 0x08-0x0F and IRQ 8-15 are 0x70-0x77.

; args: vector, handler (an offset in segment 0)
OS_SetInterruptHandler:
    push bp
    mov bp, sp
    pushf
    cli
    mov bx, [bp + 4]
    shl bx, 2
    mov ax, [bp + 8]
    mov [bx], ax
    mov word [bx + 2], 0
    popf                    ; interrupts back on if they were on
    pop bp
    ret

; args: irq
OS_EndOfInterrupt:
    push bp
    mov bp, sp
    mov al, 0x20
    cmp byte [bp + 4], 8
    jb .master
    out 0xA0, al            ; IRQ 8-15 also need the second controller told
.master:
    out 0x20, al
    pop bp
    ret

OS_EnableInterrupts:
    sti
    ret

OS_DisableInterrupts:
    cli
    ret

; args: irq
OS_UnmaskIrq:
    push bp
    mov bp, sp
    call phi_irq_port_and_bit
    in al, dx
    not ah
    and al, ah
    out dx, al
    pop bp
    ret

; args: irq
OS_MaskIrq:
    push bp
    mov bp, sp
    call phi_irq_port_and_bit
    in al, dx
    or al, ah
    out dx, al
    pop bp
    ret

; from [bp + 4] = irq: dx = the controller's mask port, ah = the irq's bit
phi_irq_port_and_bit:
    mov cl, [bp + 4]
    mov dx, 0x21
    cmp cl, 8
    jb .bit
    mov dx, 0xA1
    sub cl, 8
.bit:
    mov ah, 1
    shl ah, cl
    ret
