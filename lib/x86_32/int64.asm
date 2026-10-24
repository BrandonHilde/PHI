; provides: phi_mul64 phi_udivmod64 phi_div64 phi_mod64 phi_udiv64 phi_umod64 phi_int64_to_str phi_uint64_to_str
; requires: phi_strcpy
;
; 64-bit integers (i64, u64) in a pair of registers: edx is the high half, eax the low.
; The value on the right of an operation is in ebx:ecx. Each routine may change ebx,
; ecx, edx and eax, and keeps the rest.

; edx:eax * ebx:ecx -> edx:eax (the low 64 bits, the same signed or not)
phi_mul64:
    push esi
    push edi
    mov esi, eax            ; the left's low half
    mov edi, edx            ; and its high half
    mov eax, esi
    mul ebx                 ; left low x right high: only its low half matters
    mov ebx, eax
    mov eax, edi
    mul ecx                 ; left high x right low
    add ebx, eax
    mov eax, esi
    mul ecx                 ; left low x right low, all 64 bits
    add edx, ebx
    pop edi
    pop esi
    ret

; unsigned edx:eax / ebx:ecx -> the quotient in edx:eax, the remainder in ebx:ecx.
; Dividing by zero is the CPU's divide error, as it is for 32-bit numbers.
phi_udivmod64:
    test ebx, ebx
    jnz .divide
    test ecx, ecx
    jnz .divide
    xor eax, eax
    div eax
.divide:
    push esi
    push edi
    push ebp
    xor esi, esi            ; the remainder, in edi:esi
    xor edi, edi
    mov ebp, 64
.bit:
    ; the next bit of the dividend into the remainder (the quotient's bits come in behind)
    shl eax, 1
    rcl edx, 1
    rcl esi, 1
    rcl edi, 1
    jc .subtract            ; past 64 bits: certainly no smaller than the divisor
    cmp edi, ebx
    jb .next
    ja .subtract
    cmp esi, ecx
    jb .next
.subtract:
    sub esi, ecx
    sbb edi, ebx
    or eax, 1
.next:
    dec ebp
    jnz .bit
    mov ecx, esi
    mov ebx, edi
    pop ebp
    pop edi
    pop esi
    ret

; signed: the quotient is rounded toward zero, and the remainder has the left's sign
phi_sdivmod64:
    push esi
    xor esi, esi            ; bit 0: the quotient is negative, bit 1: the remainder is
    test edx, edx
    jns .left
    neg eax
    adc edx, 0
    neg edx
    xor esi, 3
.left:
    test ebx, ebx
    jns .right
    neg ecx
    adc ebx, 0
    neg ebx
    xor esi, 1
.right:
    call phi_udivmod64
    test esi, 1
    jz .quotient
    neg eax
    adc edx, 0
    neg edx
.quotient:
    test esi, 2
    jz .done
    neg ecx
    adc ebx, 0
    neg ebx
.done:
    pop esi
    ret

phi_div64:
    jmp phi_sdivmod64

phi_mod64:
    call phi_sdivmod64
    mov eax, ecx
    mov edx, ebx
    ret

phi_udiv64:
    jmp phi_udivmod64

phi_umod64:
    call phi_udivmod64
    mov eax, ecx
    mov edx, ebx
    ret

; Write edx:eax as decimal text to edi: signed (phi_int64_to_str) or unsigned
; (phi_uint64_to_str). ecx is the size of the destination including its terminator.
phi_uint64_to_str:
    push ecx
    push edi
    push dword 0            ; [esp] = 1 if negative
    jmp phi_int64_to_str.start

phi_int64_to_str:
    push ecx
    push edi
    push dword 0
    test edx, edx
    jns .start
    neg eax
    adc edx, 0
    neg edx
    mov dword [esp], 1
.start:
    mov esi, phi_int64_scratch + 21     ; digits are built backwards from the end
    mov byte [esi], 0
.digit:
    push esi
    xor ebx, ebx
    mov ecx, 10
    call phi_udivmod64
    pop esi
    add cl, '0'
    dec esi
    mov [esi], cl
    mov ecx, eax
    or ecx, edx
    jnz .digit
    pop ebx
    test ebx, ebx
    jz .copy
    dec esi
    mov byte [esi], '-'
.copy:
    pop edi
    pop ecx
    jmp phi_strcpy

phi_int64_scratch:
    times 22 db 0
