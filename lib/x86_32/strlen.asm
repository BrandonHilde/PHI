; provides: phi_strlen
;
; eax = length of the string at esi.
phi_strlen:
    xor eax, eax
.next:
    cmp byte [esi + eax], 0
    je .done
    inc eax
    jmp .next
.done:
    ret
