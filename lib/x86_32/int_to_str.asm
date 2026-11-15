; provides: phi_int_to_str phi_uint_to_str
; requires: phi_strcpy phi_strcpy_n
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
.digit:
    ; eax / 10 without div (which takes tens of cycles): the top of eax * 0xCCCCCCCD, shifted
    ; right 3 more, is exactly it for every 32-bit eax
    mov ecx, eax
    mov edx, 0xCCCCCCCD
    mul edx
    shr edx, 3                  ; the quotient
    mov eax, edx
    lea edx, [edx + edx*4]
    add edx, edx                ; quotient * 10
    sub ecx, edx                ; the remainder: this digit
    add cl, '0'
    dec esi
    mov [esi], cl
    test eax, eax
    jnz .digit
    test ebx, ebx
    jz .copy
    dec esi
    mov byte [esi], '-'
.copy:
    lea eax, [esp + 11]
    sub eax, esi                ; how many characters: where they end, less where they start
    mov ecx, [esp + 12]
    call phi_strcpy_n
    lea esp, [esp + 16]         ; (keeping the carry it set if it was cut)
    ret
