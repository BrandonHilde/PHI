; provides: phi_index_error
; requires: phi_throw
;
; An index was outside its array. eax = the source line, esi = the file name.
; It's an error a program can catch (if call ... throws err); one that isn't
; caught prints a message and ends the program with -1.
phi_index_error:
    mov edx, .text
    mov ebx, 1                  ; ErrorKind.INDEX
    jmp phi_throw
.text:
    db 'index out of range', 0
