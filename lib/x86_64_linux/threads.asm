; provides: phi_thread_setup phi_thread_block phi_main_thread OS_ThreadClone
; init: phi_threads_init
;
; A 64-bit Linux program's threads (user.threads) are Linux threads made with clone, as in
; 32-bit programs (x86_32_linux/threads.asm): the program's memory, files and signal
; handlers shared, on the stack the program gives each (its block at the top), and the
; thread's slot cleared by Linux when it has gone. Arguments PHI pushes are 8 bytes each.
;
; Each thread's block in a 64-bit Linux program (throw.asm's PHI_THREAD_...: its catches
; and the error it throws), reached through fs, whose base Linux keeps for each thread
; (arch_prctl), as a C library does for its own thread data.
SYS_ARCH_PRCTL      equ 158
ARCH_SET_FS         equ 0x1002
SYS_CLONE           equ 56
SYS_THREAD_EXIT     equ 60              ; exit: this thread only
CLONE_THREAD_FLAGS  equ 0x350F00        ; VM, FS, FILES, SIGHAND, THREAD, SYSVSEM, PARENT_SETTID, CHILD_CLEARTID
THREAD_BLOCK        equ 1024            ; throw.asm's PHI_THREAD_SIZE

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

; the program's first thread gets its block before any of its code runs
phi_threads_init:
    lea rax, [rel phi_main_thread]
    jmp phi_thread_setup

align 8, db 0
phi_main_thread: times 1024 db 0        ; the block of the program's first thread

; args: its frame (what phi_host_thread_main finds, at the top of its stack), where Linux
; keeps its id -> its id, or -errno. A system call in PHI couldn't do this: the new thread
; comes back from clone here too, on its new stack, and goes on to its first instructions.
OS_ThreadClone:
    push rsi
    push rdi
    mov rsi, [rsp + 24]                 ; its stack starts at its frame
    mov rdx, [rsp + 32]                 ; Linux sets its id, and clears it when it has gone
    mov r10, rdx
    mov edi, CLONE_THREAD_FLAGS
    xor r8d, r8d
    mov eax, SYS_CLONE
    syscall
    test rax, rax
    jz phi_host_thread_main             ; (the new thread, on its own stack)
    pop rdi
    pop rsi
    ret

; where a new thread starts: [rsp] = its method, [rsp + 8] = its value, and above them
; its block; when the method returns, the thread ends
phi_host_thread_main:
    lea rax, [rsp + 16]
    call phi_thread_setup
    pop rax
    call rax
    mov eax, SYS_THREAD_EXIT
    xor edi, edi
    syscall
.never:
    jmp .never
