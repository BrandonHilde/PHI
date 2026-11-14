; provides: phi_strlen phi_str_end phi_strcmp phi_str_has phi_str_has_char
;
; Text routines for 64-bit code, as x86_32's strlen, str_end, strcmp and str_has are,
; with the text's addresses in rsi and rdi.

; eax = length of the string at rsi. Every other register is left as it was (xmm0 and xmm1
; aren't: nothing keeps a value in them across a call).
;
; Sixteen bytes at a time, from the address at or before rsi that divides by 16: a text that
; has grown long is measured each time it's added to. pcmpeqb marks the bytes that are zero
; and pmovmskb gathers the marks into a number, a bit for each byte; the first sixteen's bits
; for the bytes before rsi are cleared. Reading sixteen from an address that divides by 16
; never reaches into the next page of memory. (Every 64-bit CPU has these instructions.)
phi_strlen:
    push rcx
    push rdx
    mov rax, rsi
    mov ecx, esi
    and ecx, 15
    and rax, -16
    pxor xmm0, xmm0
    movdqa xmm1, [rax]
    pcmpeqb xmm1, xmm0
    pmovmskb edx, xmm1
    shr edx, cl
    shl edx, cl
    test edx, edx
    jnz .found
.next:
    add rax, 16
    movdqa xmm1, [rax]
    pcmpeqb xmm1, xmm0
    pmovmskb edx, xmm1
    test edx, edx
    jz .next
.found:
    bsf edx, edx
    add rax, rdx
    sub rax, rsi
    pop rdx
    pop rcx
    ret

; Move rdi to the zero that ends the string there, and take as many bytes off ecx (the
; size of the destination including its terminator), so that copying to rdi with ecx
; adds to the end of the string. Leaves eax alone.
phi_str_end:
    cmp ecx, 1
    jbe .done
    cmp byte [rdi], 0
    je .done
    inc rdi
    dec ecx
    jmp phi_str_end
.done:
    ret

; Compare the strings at rsi and rdi. eax = 0 if they are equal, 1 if not.
phi_strcmp:
    mov al, [rsi]
    cmp al, [rdi]
    jne .differ
    test al, al
    jz .equal
    inc rsi
    inc rdi
    jmp phi_strcmp
.equal:
    xor eax, eax
    ret
.differ:
    mov eax, 1
    ret

; text has 'abc': eax = 1 if the string at rdi is somewhere in the string at rsi (the empty
; string is in every one), 0 if not.
phi_str_has:
    push rbx
.start:
    xor ebx, ebx                ; how far into the needle
.match:
    mov al, [rdi + rbx]
    test al, al
    jz .found                   ; all of it matched
    mov ah, [rsi + rbx]
    test ah, ah
    jz .missing                 ; the text ended first
    cmp al, ah
    jne .next
    inc ebx
    jmp .match
.next:
    inc rsi
    jmp .start
.found:
    mov eax, 1
    pop rbx
    ret
.missing:
    xor eax, eax
    pop rbx
    ret

; text has c: eax = 1 if the character in al is in the string at rsi, 0 if not (a zero
; never is).
phi_str_has_char:
    test al, al
    jz .not
.next:
    mov ah, [rsi]
    test ah, ah
    jz .not
    cmp ah, al
    je .yes
    inc rsi
    jmp .next
.yes:
    mov eax, 1
    ret
.not:
    xor eax, eax
    ret
