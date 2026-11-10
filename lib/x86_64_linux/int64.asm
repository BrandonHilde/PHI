; provides: phi_mul64 phi_udivmod64 phi_div64 phi_mod64 phi_udiv64 phi_umod64 phi_int64_to_str phi_uint64_to_str
; requires: phi_strcpy
;
; 64-bit integers (i64, u64) in 64-bit code, with the 32-bit code's interface
; (x86_32/int64.asm): edx is the high half and eax the low, and the value on the right of
; an operation is in ebx:ecx. Each routine joins the halves, works in one register, and
; splits what it gives again. Each may change rax, rbx, rcx, rdx, rsi, r8 and r9.

; rax = edx:eax
%macro PHI_JOIN_LEFT 0
    shl rdx, 32
    mov eax, eax
    or rax, rdx
%endmacro

; rcx = ebx:ecx
%macro PHI_JOIN_RIGHT 0
    shl rbx, 32
    mov ecx, ecx
    or rcx, rbx
%endmacro

; edx:eax = rax
%macro PHI_SPLIT 0
    mov rdx, rax
    shr rdx, 32
    mov eax, eax
%endmacro

; edx:eax * ebx:ecx -> edx:eax (the low 64 bits, the same signed or not)
phi_mul64:
    PHI_JOIN_LEFT
    PHI_JOIN_RIGHT
    imul rax, rcx
    PHI_SPLIT
    ret

; rdx = the remainder -> ebx:ecx = it, edx:eax = rax (the quotient)
phi_divided64:
    mov rcx, rdx
    mov rbx, rdx
    shr rbx, 32
    mov ecx, ecx
    PHI_SPLIT
    ret

; unsigned edx:eax / ebx:ecx -> the quotient in edx:eax, the remainder in ebx:ecx.
; Dividing by zero is the CPU's divide error, as it is for 32-bit numbers.
phi_udivmod64:
    PHI_JOIN_LEFT
    PHI_JOIN_RIGHT
    xor edx, edx
    div rcx
    jmp phi_divided64

; signed: the quotient is rounded toward zero, and the remainder has the left's sign
phi_sdivmod64:
    PHI_JOIN_LEFT
    PHI_JOIN_RIGHT
    cmp rcx, -1
    je .by_minus_one        ; (the smallest i64 / -1 would be the CPU's divide error: it wraps instead)
    cqo
    idiv rcx
    jmp phi_divided64
.by_minus_one:
    neg rax
    xor edx, edx
    jmp phi_divided64

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

; Write edx:eax as decimal text to rdi: signed (phi_int64_to_str) or unsigned
; (phi_uint64_to_str). ecx is the size of the destination including its terminator.
phi_uint64_to_str:
    PHI_JOIN_LEFT
    xor r9d, r9d            ; not negative
    jmp phi_int64_digits

phi_int64_to_str:
    PHI_JOIN_LEFT
    xor r9d, r9d
    test rax, rax
    jns phi_int64_digits
    neg rax                 ; (the smallest i64 stays itself, which is right unsigned)
    mov r9d, 1

; rax = the number, r9d = 1 if a '-' goes first
phi_int64_digits:
    sub rsp, 24             ; the digits, on this call's own stack (another thread may be
    lea rsi, [rsp + 21]     ; writing a number at the same time), backwards from the end
    mov byte [rsi], 0
    mov r8d, 10
.digit:
    xor edx, edx
    div r8
    add dl, '0'
    dec rsi
    mov [rsi], dl
    test rax, rax
    jnz .digit
    test r9d, r9d
    jz .copy
    dec rsi
    mov byte [rsi], '-'
.copy:
    call phi_strcpy
    lea rsp, [rsp + 24]         ; (keeping the carry it set if it was cut)
    ret
