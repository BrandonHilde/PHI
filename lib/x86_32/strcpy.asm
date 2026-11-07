; provides: phi_strcpy
;
; Copy the string at esi to edi. ecx is the size of the destination including
; its terminator; longer strings are cut short, and then the carry flag is set
; (the code generator turns it into an error where the program didn't ask for a
; cut). The result is always terminated.
phi_strcpy:
    jecxz .full
.next:
    dec ecx
    jz .last
    lodsb
    test al, al                 ; (which clears the carry)
    jz .ended
    stosb
    jmp .next
.ended:
    mov byte [edi], 0
    ret
.last:
    mov byte [edi], 0
.full:
    cmp byte [esi], 0           ; (with 0: no carry)
    je .fits
    stc
.fits:
    ret
