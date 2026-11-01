; provides: phi_index_error
; requires: phi_throw
;
; An index was outside its array (64-bit code). eax = the source line, rsi = the file
; name. One that isn't caught prints a message and ends the program with -1.
phi_index_error:
    lea rdx, [rel .text]
    mov ebx, 1                  ; ErrorKind.INDEX
    jmp phi_throw
.text:
    db 'index out of range', 0
