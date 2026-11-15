; provides: phi_str_end
; requires: phi_strlen
;
; Move edi to the zero that ends the string there, and take as many bytes off ecx (the
; size of the destination including its terminator), so that copying to edi with ecx
; adds to the end of the string. Leaves eax alone, and every register but edi and ecx.
; It moves at most ecx - 1 bytes. (Measured four bytes at a time by phi_strlen.)
phi_str_end:
    cmp ecx, 1
    jbe .done
    push eax
    push esi
    mov esi, edi
    call phi_strlen             ; eax = its length
    dec ecx                     ; the most it can move
    cmp eax, ecx
    jb .short
    mov eax, ecx                ; it fills the room: to the last place
.short:
    add edi, eax
    sub ecx, eax
    inc ecx                     ; the room after where it stopped
    pop esi
    pop eax
.done:
    ret
