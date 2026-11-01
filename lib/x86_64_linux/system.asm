; provides: phi_exit OS_Sleep OS_GetTicks OS_Yield OS_Grow
; requires: phi_flush
; init: phi_linux_start
;
; What a PHI OS program asks its kernel for, answered by 64-bit Linux system calls (the
; syscall instruction: a number in rax, arguments in rdi, rsi, rdx, r10, r8, r9; rcx and
; r11 change, every other register is kept). The first of what 64-bit programs can do
; (S10): the rest of x86_32_linux's calls come as 64-bit code learns the rest of the
; language.
;
; Arguments PHI pushes are 8 bytes each in 64-bit code: the first is at [rsp + 8].
SYS_EXIT_GROUP equ 231
SYS_BRK        equ 12
SYS_SCHED_YIELD equ 24
SYS_NANOSLEEP  equ 35
SYS_CLOCK_GETTIME equ 228
CLOCK_MONOTONIC equ 1

; called before the program's code: the time it started, which OS.GetTicks counts from
phi_linux_start:
    call phi_now
    mov [rel phi_started], eax
    ret

; ebx = exit code
phi_exit:
    call phi_flush
    mov edi, ebx
    mov eax, SYS_EXIT_GROUP
    syscall

; eax = milliseconds by the monotonic clock (only the low 32 bits: it wraps, as ticks do)
phi_now:
    push rdi
    push rsi
    mov eax, SYS_CLOCK_GETTIME
    mov edi, CLOCK_MONOTONIC
    lea rsi, [rel phi_time]
    syscall
    mov rax, [rel phi_time + 8]         ; nanoseconds
    xor edx, edx
    mov ecx, 1000000
    div rcx
    imul rcx, [rel phi_time], 1000      ; seconds
    add rax, rcx
    pop rsi
    pop rdi
    ret

; -> milliseconds since the program started
OS_GetTicks:
    call phi_now
    sub eax, [rel phi_started]
    ret

; arg: milliseconds
OS_Sleep:
    mov eax, [rsp + 8]
    test eax, eax
    jle .done
    push rdi
    push rsi
    xor edx, edx
    mov ecx, 1000
    div ecx
    mov [rel phi_time], rax
    imul edx, edx, 1000000
    mov [rel phi_time + 8], rdx
    mov eax, SYS_NANOSLEEP
    lea rdi, [rel phi_time]
    xor esi, esi
    syscall
    pop rsi
    pop rdi
.done:
    ret

OS_Yield:
    mov eax, SYS_SCHED_YIELD
    syscall
    ret

; arg: bytes -> rax = where the new memory starts (right after what the heap has), or 0.
; The heap grows by moving the program's break, a page at a time.
OS_Grow:
    push rdi
    push rsi
    mov rax, [rel phi_heap_end]
    test rax, rax
    jnz .grow
    mov eax, SYS_BRK
    xor edi, edi                        ; where the break is now
    syscall
    add rax, 4095
    and rax, ~4095
    mov [rel phi_heap_end], rax
.grow:
    mov edx, [rsp + 24]                 ; (after the two pushes and the return address)
    add rdx, 4095
    and rdx, ~4095
    add rdx, rax                        ; the new end
    push rax
    push rdx
    mov eax, SYS_BRK
    mov rdi, rdx
    syscall
    pop rdx
    pop rcx
    cmp rax, rdx
    jne .failed                         ; Linux didn't move it
    mov [rel phi_heap_end], rdx
    mov rax, rcx
    pop rsi
    pop rdi
    ret
.failed:
    xor eax, eax
    pop rsi
    pop rdi
    ret

phi_started: dd 0
align 8
phi_heap_end: dq 0                      ; the break: where the heap ends
align 8
phi_time:    dq 0, 0                    ; seconds, nanoseconds
