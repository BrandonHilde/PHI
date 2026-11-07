; provides: phi_index_error phi_text_error
; requires: phi_throw
;
; An index was outside its array (bounds checks are on outside unsafe blocks).
; eax = the source line, esi = the file name. It's an error a program can catch
; (if call ... throws err); one that isn't caught prints a message and stops.
phi_index_error:
    mov edx, .text
    mov ebx, 1                  ; ErrorKind.INDEX
    jmp phi_throw
.text:
    db 'index out of range', 0

; Text didn't fit in the str it was going to, which the program didn't ask to have cut
; (Cut.Into, in user.cut, does that). eax and esi as above.
phi_text_error:
    mov edx, .text
    mov ebx, 9                  ; ErrorKind.TEXT
    jmp phi_throw
.text:
    db "text doesn't fit in its str", 0
