; provides: phi_thread_setup phi_thread_block phi_main_thread OS_ThreadClone phi_threads_many phi_foreign_thread
; init: phi_threads_init
; requires: phi_spare_stack
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
ARCH_SET_GS         equ 0x1001
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
%ifdef PHI_DYNAMIC
    mov edi, ARCH_SET_GS                ; (fs is the C library's, in a program that loads one)
%else
    mov edi, ARCH_SET_FS
%endif
    mov eax, SYS_ARCH_PRCTL
    syscall
%ifdef PHI_DYNAMIC
    ; and under the C library's key for it, where a foreign entry looks (phi_foreign_thread)
    push r8
    push r9
    push r10
    push rbp
    mov rbp, rsp
    and rsp, -16
    mov edi, [rel phi_thread_key]       ; (rsi is still the block)
    call [rel phi_libc_pthread_setspecific]
    mov rsp, rbp
    pop rbp
    pop r10
    pop r9
    pop r8
%endif
    pop r11
    pop rdi
    pop rsi
    pop rdx
    pop rcx
    pop rax
    ret

; -> rax = the running thread's block; keeps the other registers
phi_thread_block:
%ifdef PHI_DYNAMIC
    mov rax, [gs:0]
%else
    mov rax, [fs:0]
%endif
    ret

; the program's first thread gets its block before any of its code runs
phi_threads_init:
%ifdef PHI_DYNAMIC
    ; the key every thread's block is kept under, as well as gs: a thread of a C library's
    ; own has no block until it first comes into PHI's code, and this says so
    push rcx
    push rdx
    push rsi
    push rdi
    push r8
    push r9
    push r10
    push r11
    push rbp
    mov rbp, rsp
    and rsp, -16
    lea rdi, [rel phi_thread_key]
    xor esi, esi
    call [rel phi_libc_pthread_key_create]
    mov rsp, rbp
    pop rbp
    pop r11
    pop r10
    pop r9
    pop r8
    pop rdi
    pop rsi
    pop rdx
    pop rcx
%endif
    lea rax, [rel phi_main_thread]
    jmp phi_thread_setup

align 8, db 0
phi_main_thread: times 1024 db 0        ; the block of the program's first thread

; args: its frame (what phi_host_thread_main finds, at the top of its stack), where Linux
; keeps its id -> its id, or -errno. A system call in PHI couldn't do this: the new thread
; comes back from clone here too, on its new stack, and goes on to its first instructions.
%ifdef PHI_DYNAMIC
; In a program that loads C libraries a thread is one the C library makes (pthread_create):
; the C library keeps its own data for each thread (fs), which a thread made with clone
; alone hasn't got, so nothing of C's could be called on it. It runs on a stack the C
; library gives it; of the memory the program gave it, the top holds its frame and block
; as ever, under those where its id is kept ([frame - 8]), and under that its spare stack.
; Its id's place stays as the program set it (-1) while it runs, and is made 0 when its
; method has come back: from then on it touches nothing of the program's.
SYS_SIGALTSTACK_T   equ 131
OS_ThreadClone:
    push rsi
    push rdi
    push r8
    push r9
    push r10
    push r11
    push rbp
    mov rbp, rsp
    mov rcx, [rbp + 64]                 ; its frame
    mov rax, [rbp + 72]                 ; where its id is kept
    mov [rcx - 8], rax
    sub rsp, 16
    and rsp, -16
    mov rdi, rsp                        ; where its pthread goes
    xor esi, esi
    lea rdx, [rel phi_dynamic_thread_main]
    call [rel phi_libc_pthread_create]
    test eax, eax
    jnz .failed
    mov rdi, [rsp]
    call [rel phi_libc_pthread_detach]  ; (nothing waits for it as C does: its id's place says when it has gone)
    mov eax, 1
    jmp .out
.failed:
    neg eax
    movsxd rax, eax
.out:
    mov rsp, rbp
    pop rbp
    pop r11
    pop r10
    pop r9
    pop r8
    pop rdi
    pop rsi
    ret

; where such a thread starts (the C library calls it): rdi = its frame
phi_dynamic_thread_main:
    push rbx
    push rbp
    push r12
    push r13
    push r14
    push r15
    sub rsp, 8
    mov [rsp], rdi                      ; (kept here: the method may not keep rbx)
    mov rbx, rdi
    lea rax, [rbx + 16]
    call phi_thread_setup
    lea rcx, [rbx - 16]
    mov [rax + THREAD_SPARE], rcx
    lea rax, [rbx - 16 - SPARE_STACK]
    call phi_spare_stack
    push qword [rbx + 8]
    push qword [rbx + 8]                ; (twice: the stack stays 16 bytes aligned; the method reads the lower)
    call [rbx]
    add rsp, 16
    mov rbx, [rsp]
    sub rsp, 24                         ; its spare stack no longer (it's the program's memory again): SS_DISABLE
    mov qword [rsp], 0
    mov qword [rsp + 8], 2
    mov qword [rsp + 16], 0
    mov rdi, rsp
    xor esi, esi
    mov eax, SYS_SIGALTSTACK_T
    syscall
    add rsp, 24
    mov rax, [rbx - 8]
    mov dword [rax], 0                  ; it has gone, as far as the program's memory goes
    xor eax, eax
    add rsp, 8
    pop r15
    pop r14
    pop r13
    pop r12
    pop rbp
    pop rbx
    ret

; Where outside code comes into the program (a foreign entry: C calling a PHI method) it
; may be on a thread that isn't one of the program's: a library's own, as one that plays
; sound has. A thread with no block is given one here the first time, with its spare
; stack, which it keeps for as long as it lives. Keeps every register but rax, r10 and r11
; (the ones C's values come in are still to be read).
phi_foreign_thread:
    push rbp
    mov rbp, rsp
    push rdi
    push rsi
    push rdx
    push rcx
    push r8
    push r9
    sub rsp, 128
    and rsp, -16
    movdqu [rsp], xmm0
    movdqu [rsp + 16], xmm1
    movdqu [rsp + 32], xmm2
    movdqu [rsp + 48], xmm3
    movdqu [rsp + 64], xmm4
    movdqu [rsp + 80], xmm5
    movdqu [rsp + 96], xmm6
    movdqu [rsp + 112], xmm7
    mov edi, [rel phi_thread_key]
    call [rel phi_libc_pthread_getspecific]
    test rax, rax
    jnz .have
    xor edi, edi
    mov esi, SPARE_STACK + THREAD_BLOCK
    mov edx, 3                          ; read and write
    mov r10d, 0x22                      ; private, not from a file
    mov r8, -1
    xor r9d, r9d
    mov eax, 9                          ; mmap
    syscall
    cmp rax, -4096
    ja .none
    mov dword [rel phi_threads_many], 1
    push rax
    push rax
    add rax, SPARE_STACK                ; its block, above its spare stack
    call phi_thread_setup
    mov [rax + THREAD_SPARE], rax
    pop rax
    pop rax
    call phi_spare_stack
.have:
    movdqu xmm0, [rsp]
    movdqu xmm1, [rsp + 16]
    movdqu xmm2, [rsp + 32]
    movdqu xmm3, [rsp + 48]
    movdqu xmm4, [rsp + 64]
    movdqu xmm5, [rsp + 80]
    movdqu xmm6, [rsp + 96]
    movdqu xmm7, [rsp + 112]
    lea rsp, [rbp - 48]
    pop r9
    pop r8
    pop rcx
    pop rdx
    pop rsi
    pop rdi
    pop rbp
    ret
.none:
    mov eax, 231                        ; exit_group
    mov edi, -1
    syscall

align 4
phi_thread_key: dd 0
%else
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
%endif

; where a new thread starts: [rsp] = its method, [rsp + 8] = its value, and above them
; its block; when the method returns, the thread ends. Below them is its spare stack
; (for the stack running out: system.asm's phi_fault_register), then the stack it runs on.
phi_host_thread_main:
    lea rax, [rsp + 16]
    call phi_thread_setup
    mov rbx, [rsp]
    mov r12, [rsp + 8]
    mov [rax + THREAD_SPARE], rsp
    lea rax, [rsp - SPARE_STACK]
    call phi_spare_stack
    sub rsp, SPARE_STACK
    push r12
    call rbx
    mov eax, SYS_THREAD_EXIT
    xor edi, edi
    syscall
.never:
    jmp .never

; 1 once a second thread has been started (set by the thread that starts it, before it
; exists): until then nothing else can want a lock, so locked takes one with a plain store
align 4
phi_threads_many: dd 0
