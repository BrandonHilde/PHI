; provides: phi_float_to_str phi_f16_to_f32 phi_f32_to_f16
; requires: phi_uint64_to_str phi_udivmod64 phi_int_to_str phi_strcpy
;
; Floating point numbers (f16, f32, f64) on the x87 FPU: a value being worked on is in
; st0.

; Write st0 (and take it off the FPU's stack) as decimal text to edi: up to 6 digits after
; the point, without zeros at the end (2.5, 3, -0.125), and with an exponent from 10^12 up
; and under 0.0001 (1.5e20, 2.5e-7), or nan, inf, -inf. ecx is the size of the destination
; including its terminator.
phi_float_to_str:
    push ebp
    mov ebp, esp
    sub esp, 16                 ; [ebp - 8] the millionths (a qword), [ebp - 12] the exponent
    push ecx                    ; [ebp - 20] the size
    push edi                    ; [ebp - 24] the destination
    mov dword [ebp - 12], 0
    mov edi, phi_float_scratch

    fxam                        ; what kind of number: c3 c2 c0, and c1 its sign
    fnstsw ax
    mov bl, ah
    and bl, 0x45
    cmp bl, 0x01
    je .nan                     ; not a number has no sign
    test ah, 2
    jz .positive
    mov byte [edi], '-'
    inc edi
.positive:
    cmp bl, 0x05
    je .infinity
    fabs

    ; from 10^12 up, or under 10^-4 (but not 0), it gets an exponent
    fcom qword [phi_float_big]
    fnstsw ax
    sahf
    jae .scale
    ftst
    fnstsw ax
    sahf
    je .fixed
    fcom qword [phi_float_small]
    fnstsw ax
    sahf
    jae .fixed
.scale:
    ; between 1 and 10, counting the factors of ten taken out
.down:
    fcom qword [phi_float_ten]
    fnstsw ax
    sahf
    jb .up
    fdiv qword [phi_float_ten]
    inc dword [ebp - 12]
    jmp .down
.up:
    fcom qword [phi_float_one]
    fnstsw ax
    sahf
    jae .fixed
    fmul qword [phi_float_ten]
    dec dword [ebp - 12]
    jmp .up

.fixed:
    ; the whole number of millionths, rounded
    fmul qword [phi_float_million]
    fistp qword [ebp - 8]
    mov eax, [ebp - 8]
    mov edx, [ebp - 4]
    xor ebx, ebx
    mov ecx, 1000000
    call phi_udivmod64          ; the whole part in edx:eax, the millionths in ecx
    push ecx
    mov ecx, 24
    call phi_uint64_to_str      ; edi ends at its terminator
    pop eax
    test eax, eax
    jz .exponent

    ; the point, then six digits (with their zeros in front), less the zeros at the end
    mov byte [edi], '.'
    inc edi
    mov ebx, 10
    mov esi, 6
.digit:
    xor edx, edx
    div ebx
    add dl, '0'
    mov [edi + esi - 1], dl
    dec esi
    jnz .digit
    add edi, 6
.trim:
    cmp byte [edi - 1], '0'
    jne .exponent
    dec edi
    jmp .trim

.exponent:
    mov eax, [ebp - 12]
    test eax, eax
    jz .done
    mov byte [edi], 'e'
    inc edi
    mov ecx, 12
    call phi_int_to_str
    jmp .done

.nan:
    fstp st0
    mov dword [edi], 'nan'
    add edi, 3
    jmp .done
.infinity:
    fstp st0
    mov dword [edi], 'inf'
    add edi, 3

.done:
    mov byte [edi], 0
    mov ecx, [ebp - 20]
    mov edi, [ebp - 24]
    mov esi, phi_float_scratch
    call phi_strcpy
    mov esp, ebp
    pop ebp
    ret

; ax = a half (f16) -> eax = the same number as an f32. Keeps the other registers.
phi_f16_to_f32:
    push ecx
    push edx
    movzx eax, ax
    mov edx, eax
    and edx, 0x8000
    shl edx, 16                 ; the sign
    mov ecx, eax
    shr ecx, 10
    and ecx, 0x1F               ; the exponent
    and eax, 0x3FF              ; the fraction
    cmp ecx, 0x1F
    je .special
    test ecx, ecx
    jnz .normal
    test eax, eax
    jz .signed                  ; zero
    ; a tiny (subnormal) half: shift its fraction up until it has a leading one
    mov ecx, 113
.shift:
    test eax, 0x400
    jnz .shifted
    shl eax, 1
    dec ecx
    jmp .shift
.shifted:
    and eax, 0x3FF
    jmp .build
.normal:
    add ecx, 112                ; 127 - 15
    jmp .build
.special:
    mov ecx, 255                ; infinity, or not a number
.build:
    shl ecx, 23
    shl eax, 13
    or eax, ecx
.signed:
    or eax, edx
    pop edx
    pop ecx
    ret

; eax = an f32 -> ax = the nearest half (f16); too big is infinity. Keeps the other registers.
phi_f32_to_f16:
    push ecx
    push edx
    push ebx
    mov ebx, eax
    shr ebx, 16
    and ebx, 0x8000             ; the sign
    mov ecx, eax
    shr ecx, 23
    and ecx, 0xFF               ; the exponent
    and eax, 0x7FFFFF           ; the fraction
    cmp ecx, 255
    je .special
    sub ecx, 112                ; 127 - 15
    cmp ecx, 31
    jge .infinity
    cmp ecx, 0
    jle .tiny
    ; rounded to 10 bits of fraction; a carry rounds up into the exponent, as it should
    add eax, 0x1000
    shr eax, 13
    shl ecx, 10
    add eax, ecx
    cmp eax, 0x7C00
    jae .infinity
    jmp .signed
.tiny:
    ; a subnormal half: the leading one goes in, shifted right by 14 - exponent, rounded
    cmp ecx, -10
    jl .zero
    or eax, 0x800000
    neg ecx
    add ecx, 14
    mov edx, 1
    shl edx, cl
    shr edx, 1
    add eax, edx
    shr eax, cl
    jmp .signed
.zero:
    xor eax, eax
    jmp .signed
.special:
    test eax, eax
    mov eax, 0x7C00             ; infinity ...
    jz .signed
    or eax, 0x200               ; ... or not a number
    jmp .signed
.infinity:
    mov eax, 0x7C00
.signed:
    or eax, ebx
    pop ebx
    pop edx
    pop ecx
    ret

phi_float_big:
    dq 1.0e12
phi_float_small:
    dq 1.0e-4
phi_float_ten:
    dq 10.0
phi_float_one:
    dq 1.0
phi_float_million:
    dq 1000000.0
phi_float_scratch:
    times 48 db 0
