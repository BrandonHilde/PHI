; provides: phi_read_line
; requires: phi_read_byte phi_flush
;
; Read a line of standard input into edi (the `ask` statement in a Linux or Windows
; program; phi_read_byte is each system's). ecx is the most characters to keep; the rest
; of a longer line is skipped. The terminal shows what's typed, so nothing is echoed. A
; line ends at a newline (a carriage return before it is dropped) or at the end of the
; input.
phi_read_line:
    call phi_flush          ; anything printed before the prompt
    push ecx
    xor esi, esi            ; characters kept
.next:
    call phi_read_byte
    test eax, eax
    js .done                ; the end of the input
    cmp al, 10
    je .done
    cmp al, 13
    je .next
    cmp esi, [esp]
    jae .next
    mov [edi + esi], al
    inc esi
    jmp .next
.done:
    mov byte [edi + esi], 0
    pop ecx
    ret
