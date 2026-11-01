; provides: phi_print phi_print_serial phi_console_putc phi_flush phi_write_all
;
; Output for 64-bit Linux programs, as for 32-bit ones (x86_32_linux): `log` output
; collects in a buffer and goes to standard output in one write when phi_flush runs (after
; every `log` statement); `debug` goes to standard error.
SYS_WRITE equ 1
STDOUT equ 1
STDERR equ 2
CONSOLE_BUFFER equ 4096

; rsi = zero-terminated string
phi_print:
    lodsb
    test al, al
    jz .done
    call phi_buffer_char
    jmp phi_print
.done:
    ret

; al = one character; keeps every register
phi_console_putc:
    push rbx
    call phi_buffer_char
    pop rbx
    ret

; al = one character (changes ebx)
phi_buffer_char:
    mov ebx, [rel phi_console_length]
    push rcx
    lea rcx, [rel phi_console_buffer]
    mov [rcx + rbx], al
    pop rcx
    inc ebx
    mov [rel phi_console_length], ebx
    cmp ebx, CONSOLE_BUFFER
    jb .done
    call phi_flush
.done:
    ret

; write what's buffered to standard output; keeps every register
phi_flush:
    push rax
    push rcx
    push rdx
    push rsi
    push rdi
    push r11
    mov edx, [rel phi_console_length]
    test edx, edx
    jz .done
    mov edi, STDOUT
    lea rsi, [rel phi_console_buffer]
    call phi_write_all
    mov dword [rel phi_console_length], 0
.done:
    pop r11
    pop rdi
    pop rsi
    pop rdx
    pop rcx
    pop rax
    ret

; rsi = zero-terminated string, to standard error (the `debug` statement)
phi_print_serial:
    call phi_flush          ; what was logged before it comes first
    push rdi
    xor edx, edx
.length:
    cmp byte [rsi + rdx], 0
    je .write
    inc edx
    jmp .length
.write:
    mov edi, STDERR
    call phi_write_all
    pop rdi
    ret

; edi = a file, rsi = data, edx = its length: write all of it (a pipe can take less at a
; time) -> eax = 1 if it was, 0 if writing failed. Changes rsi, rdx, rcx and r11.
phi_write_all:
    test edx, edx
    jle .done
    mov eax, SYS_WRITE
    syscall
    test rax, rax
    jle .failed
    add rsi, rax
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
