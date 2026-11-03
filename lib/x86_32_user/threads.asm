; provides: OS_ThreadStart OS_ThreadDone OS_ThreadFree
;
; A PHI OS program's threads (user.threads): each is a task of the program in the kernel
; (proc.process's system calls 44 to 46), on a stack the program gives it. It starts at
; phi_thread_main, which calls its method with its value, and ends the thread when the
; method returns (system call 11).
SYS_THREAD_END   equ 11
SYS_THREAD_START equ 44
SYS_THREAD_DONE  equ 45
SYS_THREAD_FREE  equ 46

; args: method value stackBottom stackTop -> the thread's number, or -1
OS_ThreadStart:
    mov edx, [esp + 16]                 ; the top of its stack: its method and value go there
    sub edx, 8
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
; as its argument); when the method returns, the thread has finished
phi_thread_main:
    pop eax
    call eax
    mov eax, SYS_THREAD_END
    int 0x80
.never:
    jmp .never
