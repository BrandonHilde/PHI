; provides: phi_thread_setup phi_thread_block phi_main_thread OS_ThreadStart OS_ThreadDone OS_ThreadFree
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
THREADS_MOST        equ 64              ; user.threads' MOST
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

; args: method value stackBottom stackTop -> the thread's number (1 to THREADS_MOST - 1),
; or -1
OS_ThreadStart:
    push rsi
    push rdi
    lea rcx, [rel phi_thread_used]
    mov eax, 1
.find:
    cmp byte [rcx + rax], 0
    je .found
    inc eax
    cmp eax, THREADS_MOST
    jb .find
    mov eax, -1
    jmp .out
.found:
    mov byte [rcx + rax], 1
    push rax
    mov rdx, [rsp + 56]                 ; the top of its stack: its block, and below it its
    sub rdx, THREAD_BLOCK + 16          ; method and value (the arguments: past rsi, rdi and
    mov rcx, [rsp + 32]                 ; the slot pushed here)
    mov [rdx], rcx
    mov rcx, [rsp + 40]
    mov [rdx + 8], rcx
    lea r10, [rel phi_thread_ids]       ; Linux sets it, and clears it when the thread has gone
    lea r10, [r10 + rax * 4]
    mov dword [r10], -1
    mov rsi, rdx
    mov rdx, r10
    mov edi, CLONE_THREAD_FLAGS
    xor r8d, r8d
    mov eax, SYS_CLONE
    syscall
    test rax, rax
    jz phi_host_thread_main             ; (the new thread, on its own stack)
    pop rcx
    jns .started
    lea rdx, [rel phi_thread_used]
    mov byte [rdx + rcx], 0
    lea rdx, [rel phi_thread_ids]
    mov dword [rdx + rcx * 4], 0
    mov eax, -1
    jmp .out
.started:
    mov eax, ecx
.out:
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

; arg: thread -> 1 once it has finished, 0 while it runs, -1 if it isn't one
OS_ThreadDone:
    mov ecx, [rsp + 8]
    cmp ecx, THREADS_MOST
    jae .not
    lea rdx, [rel phi_thread_used]
    cmp byte [rdx + rcx], 0
    je .not
    lea rdx, [rel phi_thread_ids]
    xor eax, eax
    cmp dword [rdx + rcx * 4], 0
    sete al
    ret
.not:
    mov eax, -1
    ret

; arg: thread -> 1 (its number given back), or 0 if it hasn't finished
OS_ThreadFree:
    push qword [rsp + 8]
    call OS_ThreadDone
    add rsp, 8
    cmp eax, 1
    jne .no
    mov ecx, [rsp + 8]
    lea rdx, [rel phi_thread_used]
    mov byte [rdx + rcx], 0
    ret
.no:
    xor eax, eax
    ret

align 4, db 0
phi_thread_ids:  times THREADS_MOST dd 0    ; each thread's Linux id while it runs, 0 once it has gone
phi_thread_used: times THREADS_MOST db 0    ; 1 for a thread number that's taken
