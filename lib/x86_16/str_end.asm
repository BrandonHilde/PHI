; provides: phi_str_end
;
; Move di to the zero that ends the string there, and take as many bytes off cx (the
; size of the destination including its terminator), so that copying to di with cx
; adds to the end of the string. Leaves eax alone.
phi_str_end:
    cmp cx, 1
    jbe .done
    cmp byte [di], 0
    je .done
    inc di
    dec cx
    jmp phi_str_end
.done:
    ret
