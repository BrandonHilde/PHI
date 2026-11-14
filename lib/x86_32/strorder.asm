; provides: phi_strorder
;
; Which of the strings at esi and edi comes first, a byte at a time, as C's strcmp says: eax is
; -1 if esi's does, 1 if edi's does, and 0 if they're the same. (A text that's the start of
; another comes before it.)
phi_strorder:
    mov al, [esi]
    cmp al, [edi]
    jb .before
    ja .after
    test al, al
    jz .same
    inc esi
    inc edi
    jmp phi_strorder
.same:
    xor eax, eax
    ret
.before:
    mov eax, -1
    ret
.after:
    mov eax, 1
    ret
