; provides: phi_int_to_str phi_uint_to_str
; requires: phi_strcpy
;
; Write eax as decimal text to di: signed (phi_int_to_str) or unsigned
; (phi_uint_to_str). cx is the size of the destination including its terminator.
phi_uint_to_str:
    push cx
    xor bx, bx              ; never negative
    jmp phi_int_to_str.start

phi_int_to_str:
    push cx
    xor bx, bx              ; bx = 1 if negative
    test eax, eax
    jns .start
    neg eax
    inc bx
.start:
    mov si, phi_int_to_str_scratch + 11   ; digits are built backwards from the end
    mov byte [si], 0
    mov ecx, 10
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

phi_int_to_str_scratch:
    times 12 db 0
