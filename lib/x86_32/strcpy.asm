; provides: phi_strcpy phi_strcpy_n
; requires: phi_strlen
;
; Copy the string at esi to edi. ecx is the size of the destination including
; its terminator; longer strings are cut short, and then the carry flag is set
; (the code generator turns it into an error where the program didn't ask for a
; cut). The result is always terminated.
;
; What it leaves, which callers rely on: edi at the terminator it wrote; ecx the room left
; after it (size - length - 1, or 0 when it was cut or just fit); esi past the source's zero,
; or at the first byte not copied (or at the zero when it just fit). Other registers are kept.
;
; The source is measured first (phi_strlen, four bytes at a time), then copied: rep movsb
; for a long one, a byte at a time for a short one, where rep movsb takes longer to start.
phi_strcpy:
    jecxz .full
    push eax
    push edx
    call phi_strlen             ; eax = its length
.measured:
    lea edx, [ecx - 1]          ; the most characters there's room for
    cmp eax, edx
    ja .cut
    sub ecx, eax
    dec ecx                     ; the room left after it
    mov edx, eax
    call .copy
    mov byte [edi], 0
    test ecx, ecx
    jz .fit                     ; (just fit: esi stays at its zero)
    inc esi                     ; past its zero
.fit:
    pop edx
    pop eax
    clc
    ret
.cut:
    call .copy                  ; edx = size - 1 of them
    mov byte [edi], 0
    xor ecx, ecx
    pop edx
    pop eax
    stc                         ; (there was more: it's longer than the room)
    ret
; edx bytes from esi to edi, moving both on; keeps ecx
.copy:
    cmp edx, 32
    jb .bytes
    push ecx
    mov ecx, edx
    rep movsb
    pop ecx
    ret
.bytes:
    test edx, edx
    jz .copied
.byte:
    mov al, [esi]
    mov [edi], al
    inc esi
    inc edi
    dec edx
    jnz .byte
.copied:
    ret
.full:
    cmp byte [esi], 0           ; (with 0: no carry)
    je .fits
    stc
.fits:
    ret

; phi_strcpy with eax = the source's length already, which it would measure first: the
; same copy, and the same registers left as they are.
phi_strcpy_n:
    test ecx, ecx
    jz phi_strcpy.full
    push eax
    push edx
    jmp phi_strcpy.measured
