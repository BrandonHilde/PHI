; provides: phi_throw phi_catch_top phi_unwind_top phi_error phi_fault_init phi_fault_entry
; requires: phi_print phi_exit phi_fault_register
; init: phi_fault_init
;
; Errors in 64-bit programs, as in 32-bit ones (x86_32_user/throw.asm): eax = the line,
; rsi = the file's name (0 if not known), rdx = the text, ebx = the kind. The error goes
; into phi_error, laid out as an Error is (kind, line, file, text, message); then a catch
; carries on, or PHI: and the message are printed and the program ends with -1.
;
; Records here are 8 bytes a field: a catch's +0 the catch before it, +8 rsp, +16 rbp,
; +24 phi_unwind_top when it was set, +32 where to go; a method's +0 the record before it,
; +8 what gives back its things, +16 its rbp.
ERROR_KIND    equ 0
ERROR_LINE    equ 4
ERROR_FILE    equ 8
ERROR_TEXT    equ 73
ERROR_MESSAGE equ 329
ERROR_SIZE    equ 713

phi_throw:
    cld
    lea rdi, [rel phi_error]
    mov [rdi + ERROR_KIND], ebx
    mov [rdi + ERROR_LINE], eax
    add rdi, ERROR_FILE
    mov ecx, 64
    call .copy
    mov rsi, rdx
    lea rdi, [rel phi_error + ERROR_TEXT]
    mov ecx, 255
    call .copy

    ; the message: the text, ' on line ', the line, then ' in ' and the file if it's known
    lea rdi, [rel phi_error + ERROR_MESSAGE]
    mov ecx, 383
    lea rsi, [rel phi_error + ERROR_TEXT]
    call .append
    cmp dword [rel phi_error + ERROR_LINE], 0
    je .said                        ; (a CPU exception's line isn't known: the text alone)
    lea rsi, [rel .on_line]
    call .append
    mov eax, [rel phi_error + ERROR_LINE]
    call .number
    cmp byte [rel phi_error + ERROR_FILE], 0
    je .said
    lea rsi, [rel .in_file]
    call .append
    lea rsi, [rel phi_error + ERROR_FILE]
    call .append
.said:
    mov byte [rdi], 0

    mov rbx, [rel phi_catch_top]
    test rbx, rbx
    jz .uncaught
.unwind:
    ; each method that's still running above the catch gives back its things
    mov rsi, [rel phi_unwind_top]
    cmp rsi, [rbx + 24]
    je .resume
    mov rax, [rsi]
    mov [rel phi_unwind_top], rax   ; off the list first
    push rbx
    mov rax, [rsi + 8]
    mov rbp, [rsi + 16]
    call rax
    pop rbx
    jmp .unwind
.resume:
    mov rax, [rbx]
    mov [rel phi_catch_top], rax    ; this catch is done
    mov rsp, [rbx + 8]
    mov rbp, [rbx + 16]
    jmp [rbx + 32]

.uncaught:
    lea rsi, [rel .prefix]
    call phi_print
    lea rsi, [rel phi_error + ERROR_MESSAGE]
    call phi_print
    lea rsi, [rel .newline]
    call phi_print
    mov ebx, -1
    jmp phi_exit

; rsi's text (none if rsi is 0) at rdi, up to ecx characters, then a zero
.copy:
    call .append
    mov byte [rdi], 0
    ret

; rsi's text (none if rsi is 0) at rdi, up to ecx characters (ecx counts down, rdi moves on)
.append:
    test rsi, rsi
    jz .appended
.next:
    test ecx, ecx
    jz .appended
    mov al, [rsi]
    test al, al
    jz .appended
    mov [rdi], al
    inc rsi
    inc rdi
    dec ecx
    jmp .next
.appended:
    ret

; eax in decimal at rdi, as .append writes
.number:
    sub rsp, 16
    lea rsi, [rsp + 15]
    mov byte [rsi], 0
    mov ebx, 10
.digit:
    xor edx, edx
    div ebx
    add dl, '0'
    dec rsi
    mov [rsi], dl
    test eax, eax
    jnz .digit
    call .append
    add rsp, 16
    ret

.prefix:
    db 13, 10, 'PHI: ', 0
.on_line:
    db ' on line ', 0
.in_file:
    db ' in ', 0
.newline:
    db 13, 10, 0

align 8
phi_catch_top:  dq 0                ; the innermost catch, or 0
phi_unwind_top: dq 0                ; the innermost running method that has things to give back, or 0
phi_error:      times ERROR_SIZE db 0

; ---- CPU exceptions, caught as errors (as x86_32_user/throw.asm has them): the system
; (system.asm's phi_fault_register) sends a program the CPU stopped here, with ebx = the
; kind of error and rcx = where it happened, and it's thrown like any other
ERROR_KIND_DIVIDE equ 3                 ; ErrorKind in lib/phi/lang/error.phi
ERROR_KIND_EMPTY  equ 4

phi_fault_init:
    lea rax, [rel phi_fault_entry]
    jmp phi_fault_register

phi_fault_entry:
    lea rdx, [rel .refused]
    cmp ebx, ERROR_KIND_DIVIDE
    jne .not_divide
    lea rdx, [rel .divided]
.not_divide:
    cmp ebx, ERROR_KIND_EMPTY
    jne .thrown
    lea rdx, [rel .empty]
.thrown:
    mov rax, rcx                        ; the line and file of where it happened, if they're known
    call phi_line_at
    jmp phi_throw
.divided: db 'division by zero', 0
.empty:   db 'an empty object or a null pointer was used', 0
.refused: db 'memory that is not there was used, or the CPU refused an instruction', 0

; rax = an address in the program's code -> eax = the line it's on, rsi = its file's name;
; 0 and 0 when it isn't PHI code (an asm block, the runtime) or isn't known (the program's
; phi_lines: a count, then each line's address, line and file, 8 bytes each)
phi_line_at:
    push rbx
    push rcx
    push rdx
    mov rcx, [rel phi_lines]
    lea rdx, [rel phi_lines + 8]
    xor ebx, ebx                        ; the last mark at or before the address
.next:
    test rcx, rcx
    jz .found
    cmp [rdx], rax
    ja .found
    mov rbx, rdx
    add rdx, 24
    dec rcx
    jmp .next
.found:
    xor eax, eax
    xor esi, esi
    test rbx, rbx
    jz .done
    mov eax, [rbx + 8]
    test eax, eax
    jz .done                            ; (past the end of the PHI code)
    mov rsi, [rbx + 16]
.done:
    pop rdx
    pop rcx
    pop rbx
    ret
