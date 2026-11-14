; provides: phi_strorder
;
; Which of the strings at si and di comes first, a byte at a time, as C's strcmp says: eax is
; -1 if si's does, 1 if di's does, and 0 if they're the same. (A text that's the start of
; another comes before it.)
phi_strorder:
    mov al, [si]
    cmp al, [di]
    jb .before
    ja .after
    test al, al
    jz .same
    inc si
    inc di
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
