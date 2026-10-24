; provides: phi_out_of_memory
; requires: phi_print phi_print_int phi_exit
;
; The heap had no room for a new object, and the kernel had no more to give it.
; [phi_object_line] = the source line of the statement that asked (set by the
; program's object code). Print a message and end the program with -1.
phi_out_of_memory:
    mov esi, .message
    call phi_print
    mov eax, [phi_object_line]
    call phi_print_int
    mov esi, .newline
    call phi_print
    mov ebx, -1
    jmp phi_exit
.message:
    db 13, 10, 'PHI: out of memory for a new object on line ', 0
.newline:
    db 13, 10, 0
