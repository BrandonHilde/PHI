; provides: phi_strcpy
;
; Copy the string at esi to edi. ecx is the size of the destination including
; its terminator; longer strings are cut short. The result is always terminated.
phi_strcpy:
    jecxz .done
.next:
    dec ecx
    jz .terminate
    lodsb
    test al, al
    jz .terminate
    stosb
    jmp .next
.terminate:
    mov byte [edi], 0
.done:
    ret
