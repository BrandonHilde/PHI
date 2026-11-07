; provides: phi_index_error phi_text_error
; requires: phi_print phi_print_int
;
; An index was outside its array (bounds checks are on outside unsafe blocks).
; eax = the source line, si = the file name. Prints a message and stops.
phi_index_error:
    mov dx, .message
.stop:
    push si
    push eax
    mov si, dx
    call phi_print
    pop eax
    call phi_print_int
    mov si, phi_text_error.in_file
    call phi_print
    pop si
    call phi_print
    mov si, phi_text_error.newline
    call phi_print
    cli
.halt:
    hlt
    jmp .halt
.message:
    db 13, 10, 'PHI: index out of range on line ', 0

; Text didn't fit in the str it was going to. eax and si as above; prints and stops too.
phi_text_error:
    mov dx, .message
    jmp phi_index_error.stop
.message:
    db 13, 10, "PHI: text doesn't fit in its str on line ", 0

.in_file:
    db ' in ', 0
.newline:
    db 13, 10, 0
