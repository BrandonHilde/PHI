; provides: phi_str_has phi_str_has_char
;
; text has 'abc': eax = 1 if the string at edi is somewhere in the string at esi (the empty
; string is in every one), 0 if not.
phi_str_has:
    push ebx
.start:
    mov ebx, 0                  ; how far into the needle
.match:
    mov al, [edi + ebx]
    test al, al
    jz .found                   ; all of it matched
    mov ah, [esi + ebx]
    test ah, ah
    jz .missing                 ; the text ended first
    cmp al, ah
    jne .next
    inc ebx
    jmp .match
.next:
    inc esi
    jmp .start
.found:
    mov eax, 1
    pop ebx
    ret
.missing:
    xor eax, eax
    pop ebx
    ret

; text has c: eax = 1 if the character in al is in the string at esi, 0 if not (a zero
; never is).
phi_str_has_char:
    test al, al
    jz .not
.next:
    mov ah, [esi]
    test ah, ah
    jz .not
    cmp ah, al
    je .yes
    inc esi
    jmp .next
.yes:
    mov eax, 1
    ret
.not:
    xor eax, eax
    ret
