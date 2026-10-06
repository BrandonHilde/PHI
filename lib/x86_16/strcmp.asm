; provides: phi_strcmp
;
; Compare the strings at si and di. eax = 0 if they are equal, 1 if not.
phi_strcmp:
    mov al, [si]
    cmp al, [di]
    jne .differ
    test al, al
    jz .equal
    inc si
    inc di
    jmp phi_strcmp
.equal:
    xor eax, eax
    ret
.differ:
    mov eax, 1
    ret
