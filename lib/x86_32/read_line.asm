; provides: phi_read_line
; requires: phi_read_char phi_console_putc
;
; Read a line from the keyboard driver into edi, echoing it. ecx is the most
; characters to keep. Enter ends the line; backspace works. (The `ask` statement.)
phi_read_line:
    xor ebx, ebx            ; characters so far
.key:
    push ebx
    push ecx
    push edi
    call phi_read_char
    pop edi
    pop ecx
    pop ebx
    cmp al, 13
    je .done
    cmp al, 8
    je .backspace
    cmp ebx, ecx
    jae .key                ; full: ignore until Enter
    mov [edi + ebx], al
    inc ebx
    call phi_console_putc
    jmp .key
.backspace:
    test ebx, ebx
    jz .key
    dec ebx
    call phi_console_putc   ; the console steps back and erases
    jmp .key
.done:
    mov byte [edi + ebx], 0
    ret
