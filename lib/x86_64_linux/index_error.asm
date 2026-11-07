; provides: phi_index_error phi_text_error
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

; Text didn't fit in the str it was going to, which the program didn't ask to have cut
; (Cut.Into, in user.cut, does that). eax and rsi as above.
phi_text_error:
    lea rdx, [rel .text]
    mov ebx, 9                  ; ErrorKind.TEXT
    jmp phi_throw
.text:
    db "text doesn't fit in its str", 0
