; provides: phi_strcpy
;
; Copy the string at si to di. cx is the size of the destination including its
; terminator; longer strings are cut short, and then the carry flag is set (the
; code generator turns it into an error where the program didn't ask for a cut).
; The result is always terminated.
phi_strcpy:
    jcxz .full
.next:
    dec cx
    jz .last
    lodsb
    test al, al                 ; (which clears the carry)
    jz .ended
    stosb
    jmp .next
.ended:
    mov byte [di], 0
    ret
.last:
    mov byte [di], 0
.full:
    cmp byte [si], 0            ; (with 0: no carry)
    je .fits
    stc
.fits:
    ret
