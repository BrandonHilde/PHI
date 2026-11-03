; provides: phi_thread_setup phi_thread_block
;
; Each thread's block in a 64-bit Linux program (throw.asm's PHI_THREAD_...: its catches
; and the error it throws), reached through fs, whose base Linux keeps for each thread
; (arch_prctl), as a C library does for its own thread data.
SYS_ARCH_PRCTL      equ 158
ARCH_SET_FS         equ 0x1002

; rax = a block of PHI_THREAD_SIZE bytes: the running thread's from now on (no catches
; yet), reached through fs; keeps the other registers
phi_thread_setup:
    mov [rax], rax                      ; PHI_THREAD_SELF: its own address
    mov qword [rax + 8], 0              ; PHI_THREAD_CATCH
    mov qword [rax + 16], 0             ; PHI_THREAD_UNWIND
    push rax
    push rcx
    push rdx
    push rsi
    push rdi
    push r11
    mov rsi, rax
    mov edi, ARCH_SET_FS
    mov eax, SYS_ARCH_PRCTL
    syscall
    pop r11
    pop rdi
    pop rsi
    pop rdx
    pop rcx
    pop rax
    ret

; -> rax = the running thread's block; keeps the other registers
phi_thread_block:
    mov rax, [fs:0]
    ret
