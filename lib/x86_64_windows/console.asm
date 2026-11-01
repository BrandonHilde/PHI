; provides: phi_print phi_print_serial phi_console_putc phi_flush phi_write_all
; requires: phi_imports
;
; Output for 64-bit Windows programs, as for 32-bit ones (x86_32_windows/console.asm): `log`
; output collects in a buffer and goes to standard output in one write when phi_flush runs
; (after every `log` statement); `debug` goes to standard error. Text is written as it is,
; a byte a character.
STD_OUTPUT_HANDLE equ -11
STD_ERROR_HANDLE equ -12
CONSOLE_BUFFER equ 4096

; A call to kernel32's function %1, with the stack 16-byte aligned and its 32 bytes of
; shadow space, and any arguments past the fourth (%2, %3, %4: registers other than rsp
; and r12, or numbers) on the stack after them. The first four go in rcx, rdx, r8 and r9
; first. r12 keeps where the stack was (kernel32 keeps r12; PHI's code doesn't use it).
; (system.asm has the same, for whichever of them comes first.)
%ifnmacro PHI_CALL 1-4
%macro PHI_CALL 1-4
    mov r12, rsp
    and rsp, -16
    sub rsp, 64
%if %0 >= 2
    mov qword [rsp + 32], %2
%endif
%if %0 >= 3
    mov qword [rsp + 40], %3
%endif
%if %0 >= 4
    mov qword [rsp + 48], %4
%endif
    call [rel phi_imp_%1]
    mov rsp, r12
%endmacro
%endif

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
    push rbx
    push rcx
    push rdx
    push rsi
    push r8
    push r9
    push r10
    push r11
    push r12
    mov edx, [rel phi_console_length]
    test edx, edx
    jz .done
    mov ecx, STD_OUTPUT_HANDLE
    PHI_CALL GetStdHandle
    mov rbx, rax
    lea rsi, [rel phi_console_buffer]
    mov edx, [rel phi_console_length]
    call phi_write_all
    mov dword [rel phi_console_length], 0
.done:
    pop r12
    pop r11
    pop r10
    pop r9
    pop r8
    pop rsi
    pop rdx
    pop rcx
    pop rbx
    pop rax
    ret

; rsi = zero-terminated string, to standard error (the `debug` statement)
phi_print_serial:
    call phi_flush          ; what was logged before it comes first
    push rsi
    mov ecx, STD_ERROR_HANDLE
    PHI_CALL GetStdHandle
    pop rsi
    mov rbx, rax
    xor edx, edx
.length:
    cmp byte [rsi + rdx], 0
    je .write
    inc edx
    jmp .length
.write:
    jmp phi_write_all

; rbx = a handle (a file, or standard output), rsi = data, edx = its length: write all of
; it (a pipe can take less at a time) -> eax = 1 if it was, 0 if writing failed. Changes
; rsi, rdx, rcx, r8 to r12.
phi_write_all:
    test edx, edx
    jle .done
    push rsi
    push rdx
    mov rcx, rbx
    mov r8d, edx
    mov rdx, rsi
    lea r9, [rel phi_written]
    PHI_CALL WriteFile, 0           ; not overlapped
    pop rdx
    pop rsi
    test eax, eax
    jz .failed
    mov eax, [rel phi_written]
    test eax, eax
    jz .failed
    add rsi, rax
    sub edx, eax
    jmp phi_write_all
.done:
    mov eax, 1
    ret
.failed:
    xor eax, eax
    ret

align 4
phi_written: dd 0
phi_console_length: dd 0
phi_console_buffer: times CONSOLE_BUFFER db 0
