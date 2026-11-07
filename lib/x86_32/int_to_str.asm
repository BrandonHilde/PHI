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
    sub esp, 12             ; the digits, on this call's own stack (another core, or thread,
    lea esi, [esp + 11]     ; may be writing a number at the same time), backwards from the end
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
    mov ecx, [esp + 12]
    call phi_strcpy
    lea esp, [esp + 16]         ; (keeping the carry it set if it was cut)
    ret
