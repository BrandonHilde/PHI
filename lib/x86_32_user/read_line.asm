; provides: phi_read_line
; requires: phi_read_char phi_console_putc phi_flush
;
; Read a line from the keyboard into edi, echoing it (the `ask` statement in a
; user program). ecx is the most characters to keep.
phi_read_line:
    xor ebx, ebx
.key:
    call phi_flush          ; anything printed before the prompt
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
    jae .key
    mov [edi + ebx], al
    inc ebx
    call phi_console_putc
    call phi_flush          ; show it straight away
    jmp .key
.backspace:
    test ebx, ebx
    jz .key
    dec ebx
    call phi_console_putc
    call phi_flush
    jmp .key
.done:
    mov byte [edi + ebx], 0
    ret
