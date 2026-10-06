; provides: phi_index_error
; requires: phi_print phi_print_int phi_exit
;
; An index was outside its array: print a message and end the program with -1.
phi_index_error:
    push eax
    mov esi, .message
    call phi_print
    pop eax
    call phi_print_int
    mov esi, .newline
    call phi_print
    mov ebx, -1
    jmp phi_exit
.message:
    db 13, 10, 'PHI: index out of range on line ', 0
.newline:
    db 13, 10, 0
