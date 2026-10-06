; provides: phi_strlen
;
; eax = length of the string at si.
phi_strlen:
    xor eax, eax
.next:
    cmp byte [si], 0
    je .done
    inc si
    inc eax
    jmp .next
.done:
    ret
