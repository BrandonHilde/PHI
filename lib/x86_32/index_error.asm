; provides: phi_index_error
; requires: phi_print phi_print_int
;
; An index was outside its array (bounds checks are on outside unsafe blocks).
; eax = the source line, esi = the file name. Prints a message and stops.
phi_index_error:
    push esi
    push eax
    mov esi, .message
    call phi_print
    pop eax
    call phi_print_int
    mov esi, .in_file
    call phi_print
    pop esi
    call phi_print
    mov esi, .newline
    call phi_print
    cli
.halt:
    hlt
    jmp .halt
.message:
    db 13, 10, 'PHI: index out of range on line ', 0
.in_file:
    db ' in ', 0
.newline:
    db 13, 10, 0
