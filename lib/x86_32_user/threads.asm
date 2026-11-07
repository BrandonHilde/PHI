; provides: OS_ThreadStart OS_ThreadDone OS_ThreadFree phi_thread_setup phi_thread_block phi_main_thread
; init: phi_threads_init
;
; A PHI OS program's threads (user.threads): each is a task of the program in the kernel
; (proc.process's system calls 44 to 46), on a stack the program gives it. The top of the
; stack is the thread's block (its catches and the error it throws: throw.asm's
; PHI_THREAD_...), which the kernel gives a segment of its own (system call 12) that the
; thread reaches through gs. It starts at phi_thread_main, which calls its method with its
; value, and ends the thread when the method returns (system call 11). Under them is its
; spare stack, where the kernel sends it when its stack runs out, then the stack it runs on.
SYS_THREAD_END   equ 11
SYS_THREAD_BLOCK equ 12
SYS_THREAD_START equ 44
SYS_THREAD_DONE  equ 45
SYS_THREAD_FREE  equ 46
THREAD_BLOCK     equ 1024               ; throw.asm's PHI_THREAD_SIZE
THREAD_SPARE     equ 16384

; args: method value stackBottom stackTop -> the thread's number, or -1
OS_ThreadStart:
    mov edx, [esp + 16]                 ; the top of its stack: its block, and below it its
    sub edx, THREAD_BLOCK + 8           ; method and value
    mov eax, [esp + 4]
    mov [edx], eax
    mov eax, [esp + 8]
    mov [edx + 4], eax
    mov ebx, phi_thread_main
    mov ecx, edx
    mov edx, [esp + 12]
    mov esi, [esp + 16]
    mov eax, SYS_THREAD_START
    int 0x80
    ret

; arg: thread -> 1 once it has finished, 0 while it runs, -1 if it isn't this program's
OS_ThreadDone:
    mov ebx, [esp + 4]
    mov eax, SYS_THREAD_DONE
    int 0x80
    ret

; arg: thread -> 1 (its number given back), or 0 if it hasn't finished
OS_ThreadFree:
    mov ebx, [esp + 4]
    mov eax, SYS_THREAD_FREE
    int 0x80
    ret

; where a thread starts: [esp] = its method, [esp + 4] = its value (which the method finds
; as its argument), and above them its block; when the method returns, it has finished
phi_thread_main:
    lea eax, [esp + 8]
    call phi_thread_setup
    mov eax, [esp]
    mov ebx, [esp + 4]
    sub esp, THREAD_SPARE
    push ebx
    call eax
    mov eax, SYS_THREAD_END
    int 0x80
.never:
    jmp .never

; eax = a block of THREAD_BLOCK bytes: the running thread's from now on (no catches yet),
; reached through gs; keeps the other registers
phi_thread_setup:
    mov [eax], eax                      ; PHI_THREAD_SELF: its own address
    mov dword [eax + 4], 0              ; PHI_THREAD_CATCH
    mov dword [eax + 8], 0              ; PHI_THREAD_UNWIND
    push ebx
    mov ebx, eax
    mov eax, SYS_THREAD_BLOCK
    int 0x80
    test eax, eax
    js .done
    mov gs, ax
.done:
    mov eax, ebx
    pop ebx
    ret

; -> eax = the running thread's block; keeps the other registers
phi_thread_block:
    mov eax, [gs:0]
    ret

; the program's first thread gets its block before any of its code runs
phi_threads_init:
    mov eax, phi_main_thread
    jmp phi_thread_setup

align 4, db 0
phi_main_thread: times 1024 db 0        ; the block of the program's first thread
