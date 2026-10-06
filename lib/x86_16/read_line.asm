; provides: phi_read_line
; requires: phi_serial_putc
;
; Read a line from the keyboard (BIOS) into di, echoing it. cx is the most
; characters to keep. Enter ends the line; backspace works.
phi_read_line:
    xor bx, bx              ; characters so far
.key:
    xor ah, ah
    int 0x16
    cmp al, 0x0D
    je .done
    cmp al, 0x08
    je .backspace
    cmp bx, cx
    jae .key                ; full: ignore until Enter
    mov [di+bx], al
    inc bx
    call .echo
    jmp .key
.backspace:
    test bx, bx
    jz .key
    dec bx
    mov al, 0x08
    call .echo
    mov al, ' '
    call .echo
    mov al, 0x08
    call .echo
    jmp .key
.done:
    mov byte [di+bx], 0
    ret
.echo:
    push bx
    push ax
    mov ah, 0x0E
    mov bx, 0x000F
    int 0x10
    pop ax
    pop bx
    jmp phi_serial_putc
