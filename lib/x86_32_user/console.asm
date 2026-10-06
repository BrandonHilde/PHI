; provides: phi_print phi_print_serial phi_console_putc phi_flush
;
; Output for user programs. Screen output collects in a buffer and goes to the
; kernel in one system call when phi_flush runs (after every `log` statement),
; so lines from programs running at the same time don't get mixed up.
; System call 1 writes a string to the screen and COM1, 2 to COM1 only.
SYS_WRITE equ 1
SYS_DEBUG equ 2
CONSOLE_BUFFER equ 256

; esi = zero-terminated string
phi_print:
    lodsb
    test al, al
    jz .done
    call phi_buffer_char
    jmp phi_print
.done:
    ret

; al = one character; preserves all registers
phi_console_putc:
    pushad
    call phi_buffer_char
    popad
    ret

phi_buffer_char:
    mov ebx, [phi_console_length]
    mov [phi_console_buffer + ebx], al
    inc ebx
    mov [phi_console_length], ebx
    cmp ebx, CONSOLE_BUFFER - 1
    jb .done
    call phi_flush
.done:
    ret

; send what's buffered to the kernel; preserves all registers
phi_flush:
    pushad
    mov ebx, [phi_console_length]
    test ebx, ebx
    jz .done
    mov byte [phi_console_buffer + ebx], 0
    mov ebx, phi_console_buffer
    mov eax, SYS_WRITE
    int 0x80
    mov dword [phi_console_length], 0
.done:
    popad
    ret

; esi = zero-terminated string, straight to COM1 (the `debug` statement)
phi_print_serial:
    mov ebx, esi
    mov eax, SYS_DEBUG
    int 0x80
    ret

phi_console_length: dd 0
phi_console_buffer: times CONSOLE_BUFFER db 0
