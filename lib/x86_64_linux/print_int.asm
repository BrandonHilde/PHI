; provides: phi_print_int phi_print_uint phi_print_int_serial phi_print_uint_serial phi_int_to_str phi_uint_to_str phi_strcpy phi_strcpy_n
; requires: phi_print phi_print_serial phi_strlen
;
; Numbers as text, for 64-bit code (x86_32's print_int, print_int_serial, int_to_str and
; strcpy, with 64-bit addresses): eax in decimal, signed or unsigned, to the screen and
; COM1 (on Linux, standard output) or to COM1 only (standard error).
phi_print_int:
    call phi_int_text
    jmp phi_print

phi_print_uint:
    call phi_uint_text
    jmp phi_print

phi_print_int_serial:
    call phi_int_text
    jmp phi_print_serial

phi_print_uint_serial:
    call phi_uint_text
    jmp phi_print_serial

; rsi = eax as text, in a buffer of its own
phi_int_text:
    lea rdi, [rel phi_print_int_text]
    mov ecx, 12
    call phi_int_to_str
    lea rsi, [rel phi_print_int_text]
    ret

phi_uint_text:
    lea rdi, [rel phi_print_int_text]
    mov ecx, 12
    call phi_uint_to_str
    lea rsi, [rel phi_print_int_text]
    ret

; Write eax as decimal text to rdi: signed (phi_int_to_str) or unsigned (phi_uint_to_str).
; ecx is the size of the destination including its terminator.
phi_uint_to_str:
    push rcx
    xor ebx, ebx            ; never negative
    jmp phi_int_to_str.start

phi_int_to_str:
    push rcx
    xor ebx, ebx            ; ebx = 1 if negative
    test eax, eax
    jns .start
    neg eax
    inc ebx
.start:
    sub rsp, 16             ; the digits, on this call's own stack (another thread may be
    lea rsi, [rsp + 11]     ; writing a number at the same time), backwards from the end
    mov byte [rsi], 0
    mov eax, eax                ; (all of rax is the number)
.digit:
    ; eax / 10 without div (which takes tens of cycles): eax * 0xCCCCCCCD, shifted right 35,
    ; is exactly it for every 32-bit eax
    mov ecx, eax
    mov edx, 0xCCCCCCCD
    imul rdx, rcx
    shr rdx, 35                 ; the quotient
    mov eax, edx
    lea edx, [rdx + rdx*4]
    add edx, edx                ; quotient * 10
    sub ecx, edx                ; the remainder: this digit
    add cl, '0'
    dec rsi
    mov [rsi], cl
    test eax, eax
    jnz .digit
    test ebx, ebx
    jz .copy
    dec rsi
    mov byte [rsi], '-'
.copy:
    lea rax, [rsp + 11]
    sub rax, rsi                ; how many characters: where they end, less where they start
    mov rcx, [rsp + 16]
    call phi_strcpy_n
    lea rsp, [rsp + 24]         ; (keeping the carry it set if it was cut)
    ret

; Copy the string at rsi to rdi. ecx is the size of the destination including its
; terminator; longer strings are cut short, and then the carry flag is set (the code
; generator turns it into an error where the program didn't ask for a cut). The result
; is always terminated.
phi_strcpy:
    test ecx, ecx
    jz .full
    push rax
    push rdx
    call phi_strlen             ; eax = its length (see x86_32's strcpy.asm for what this leaves)
.measured:
    lea edx, [rcx - 1]          ; the most characters there's room for
    cmp eax, edx
    ja .cut
    sub ecx, eax
    dec ecx                     ; the room left after it
    mov edx, eax
    call .copy
    mov byte [rdi], 0
    test ecx, ecx
    jz .fit                     ; (just fit: rsi stays at its zero)
    inc rsi                     ; past its zero
.fit:
    pop rdx
    pop rax
    clc
    ret
.cut:
    call .copy                  ; edx = size - 1 of them
    mov byte [rdi], 0
    xor ecx, ecx
    pop rdx
    pop rax
    stc                         ; (there was more: it's longer than the room)
    ret
; edx bytes from rsi to rdi, moving both on; keeps rcx. Up to 32 of them as two moves that
; may overlap (the first bytes and the last, of 16, 8, 4 or 1 at a time), which reach no byte
; outside the edx (a text's are short, and rep movsb takes longer to start); more, rep movsb.
; xmm0 and xmm1 change, as phi_strlen changes them.
.copy:
    cmp edx, 32
    ja .many
    cmp edx, 16
    jae .sixteen
    cmp edx, 8
    jae .eight
    cmp edx, 4
    jae .four
    test edx, edx
    jz .copied
.byte:
    mov al, [rsi]
    mov [rdi], al
    inc rsi
    inc rdi
    dec edx
    jnz .byte
.copied:
    ret
.sixteen:
    movdqu xmm0, [rsi]
    movdqu xmm1, [rsi + rdx - 16]
    movdqu [rdi], xmm0
    movdqu [rdi + rdx - 16], xmm1
    jmp .moved
.eight:
    movq xmm0, [rsi]
    movq xmm1, [rsi + rdx - 8]
    movq [rdi], xmm0
    movq [rdi + rdx - 8], xmm1
    jmp .moved
.four:
    movd xmm0, [rsi]
    movd xmm1, [rsi + rdx - 4]
    movd [rdi], xmm0
    movd [rdi + rdx - 4], xmm1
.moved:
    add rsi, rdx
    add rdi, rdx
    ret
.many:
    push rcx
    mov ecx, edx
    rep movsb
    pop rcx
    ret
.full:
    cmp byte [rsi], 0           ; (with 0: no carry)
    je .fits
    stc
.fits:
    ret

; phi_strcpy with eax = the source's length already, which it would measure first: the
; same copy, and the same registers left as they are.
phi_strcpy_n:
    test ecx, ecx
    jz phi_strcpy.full
    push rax
    push rdx
    jmp phi_strcpy.measured

phi_print_int_text:      times 12 db 0
