; provides: phi_out_of_memory
; requires: phi_throw
;
; The heap had no room for a new object, and the kernel had no more to give it.
; [phi_object_line] = the source line of the statement that asked (set by the
; program's object code). It's an error a program can catch (if call ... throws
; err); one that isn't caught prints a message and ends the program with -1.
phi_out_of_memory:
    mov eax, [phi_object_line]
    xor esi, esi                ; the file isn't known
    mov edx, .text
    mov ebx, 2                  ; ErrorKind.MEMORY
    jmp phi_throw
.text:
    db 'out of memory for a new object', 0
