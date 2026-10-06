; provides: phi_strcmp
;
; Compare the strings at esi and edi. eax = 0 if they are equal, 1 if not.
phi_strcmp:
    mov al, [esi]
    cmp al, [edi]
    jne .differ
    test al, al
    jz .equal
    inc esi
    inc edi
    jmp phi_strcmp
.equal:
    xor eax, eax
    ret
.differ:
    mov eax, 1
    ret
