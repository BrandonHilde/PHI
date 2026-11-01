; provides: phi_out_of_memory
; requires: phi_throw
;
; The heap had no room for a new object (64-bit code), as in x86_32_user/out_of_memory.asm:
; [phi_object_line] = the source line of the statement that asked. One that isn't caught
; prints a message and ends the program with -1.
phi_out_of_memory:
    mov eax, [rel phi_object_line]
    xor esi, esi                ; the file isn't known
    lea rdx, [rel .text]
    mov ebx, 2                  ; ErrorKind.MEMORY
    jmp phi_throw
.text:
    db 'out of memory for a new object', 0
