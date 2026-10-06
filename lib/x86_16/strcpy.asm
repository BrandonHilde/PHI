; provides: phi_strcpy
;
; Copy the string at si to di. cx is the size of the destination including its
; terminator; longer strings are cut short. The result is always terminated.
phi_strcpy:
    jcxz .done
.next:
    dec cx
    jz .terminate
    lodsb
    test al, al
    jz .terminate
    stosb
    jmp .next
.terminate:
    mov byte [di], 0
.done:
    ret
