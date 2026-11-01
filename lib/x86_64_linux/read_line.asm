; provides: phi_read_line
; requires: phi_read_byte phi_flush
;
; Read a line of standard input into rdi (the `ask` statement in a 64-bit Linux program),
; as x86_32_linux/read_line.asm does: ecx is the most characters to keep; the rest of a
; longer line is skipped. A line ends at a newline (a carriage return before it is
; dropped) or at the end of the input.
phi_read_line:
    call phi_flush          ; anything printed before the prompt
    push rcx
    xor esi, esi            ; characters kept
.next:
    call phi_read_byte      ; (keeps rsi and rdi)
    test eax, eax
    js .done                ; the end of the input
    cmp al, 10
    je .done
    cmp al, 13
    je .next
    cmp esi, [rsp]
    jae .next
    mov [rdi + rsi], al
    inc esi
    jmp .next
.done:
    mov byte [rdi + rsi], 0
    pop rcx
    ret
