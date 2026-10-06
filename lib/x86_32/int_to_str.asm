; provides: phi_int_to_str phi_uint_to_str
; requires: phi_strcpy
;
; Write eax as decimal text to edi: signed (phi_int_to_str) or unsigned
; (phi_uint_to_str). ecx is the size of the destination including its terminator.
phi_uint_to_str:
    push ecx
    xor ebx, ebx            ; never negative
    jmp phi_int_to_str.start

phi_int_to_str:
    push ecx
    xor ebx, ebx            ; ebx = 1 if negative
    test eax, eax
    jns .start
    neg eax
    inc ebx
.start:
    mov esi, phi_int_to_str_scratch + 11   ; digits are built backwards from the end
    mov byte [esi], 0
    mov ecx, 10
.digit:
    xor edx, edx
    div ecx
    add dl, '0'
    dec esi
    mov [esi], dl
    test eax, eax
    jnz .digit
    test ebx, ebx
    jz .copy
    dec esi
    mov byte [esi], '-'
.copy:
    pop ecx
    jmp phi_strcpy

phi_int_to_str_scratch:
    times 12 db 0
