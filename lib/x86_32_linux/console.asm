; provides: phi_print phi_print_serial phi_console_putc phi_flush phi_write_all
;
; Output for Linux programs. `log` output collects in a buffer and goes to standard
; output in one write when phi_flush runs (after every `log` statement), as it goes to
; the kernel on PHI OS. `debug` goes to standard error, which is to a Linux program what
; the serial port is to a PHI OS one: beside the screen, not on it.
SYS_WRITE equ 4
STDOUT equ 1
STDERR equ 2
CONSOLE_BUFFER equ 4096

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
    cmp ebx, CONSOLE_BUFFER
    jb .done
    call phi_flush
.done:
    ret

; write what's buffered to standard output; preserves all registers
phi_flush:
    pushad
    mov edx, [phi_console_length]
    test edx, edx
    jz .done
    mov ebx, STDOUT
    mov ecx, phi_console_buffer
    call phi_write_all
    mov dword [phi_console_length], 0
.done:
    popad
    ret

; esi = zero-terminated string, to standard error (the `debug` statement)
phi_print_serial:
    call phi_flush          ; what was logged before it comes first
    mov ecx, esi
    xor edx, edx
.length:
    cmp byte [esi + edx], 0
    je .write
    inc edx
    jmp .length
.write:
    mov ebx, STDERR
    jmp phi_write_all

; ebx = a file, ecx = data, edx = its length: write all of it (a pipe can take less at a
; time) -> eax = 1 if it was, 0 if writing failed. Changes ecx and edx.
phi_write_all:
    test edx, edx
    jle .done
    mov eax, SYS_WRITE
    int 0x80
    test eax, eax
    jle .failed
    add ecx, eax
    sub edx, eax
    jmp phi_write_all
.done:
    mov eax, 1
    ret
.failed:
    xor eax, eax
    ret

phi_console_length: dd 0
phi_console_buffer: times CONSOLE_BUFFER db 0
