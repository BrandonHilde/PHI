; provides: phi_str_end
;
; Move edi to the zero that ends the string there, and take as many bytes off ecx (the
; size of the destination including its terminator), so that copying to edi with ecx
; adds to the end of the string. Leaves eax alone.
phi_str_end:
    cmp ecx, 1
    jbe .done
    cmp byte [edi], 0
    je .done
    inc edi
    dec ecx
    jmp phi_str_end
.done:
    ret
