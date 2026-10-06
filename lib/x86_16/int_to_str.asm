; provides: phi_int_to_str
; requires: phi_strcpy
;
; Write eax as signed decimal text to di. cx is the size of the destination
; including its terminator.
phi_int_to_str:
    push cx
    mov si, .scratch + 11   ; digits are built backwards from the end
    mov byte [si], 0
    mov ecx, 10
    xor bx, bx              ; bx = 1 if negative
    test eax, eax
    jns .digit
    neg eax
    inc bx
.digit:
    xor edx, edx
    div ecx
    add dl, '0'
    dec si
    mov [si], dl
    test eax, eax
    jnz .digit
    test bx, bx
    jz .copy
    dec si
    mov byte [si], '-'
.copy:
    pop cx
    jmp phi_strcpy
.scratch:
    times 12 db 0
