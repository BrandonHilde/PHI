; provides: phi_thread_setup phi_thread_block phi_tls_index phi_main_thread OS_ThreadStart OS_ThreadDone OS_ThreadFree
; init: phi_threads_init
; requires: phi_imports phi_print phi_exit
;
; Each thread's block in a Windows program (throw.asm's PHI_THREAD_...: its catches and
; the error it throws). Windows keeps fs (and gs) for itself, so the block's address is in
; a TLS slot of the thread's TEB: TlsSlots, at fs:0xE10, for the first 64 slots, which the
; code generator reads straight from there (phi_tls_index says which).
;
; A Windows program's threads (user.threads) are Windows threads (CreateThread), on the
; stacks Windows gives them; of what the program gives each, the top holds its block and
; what it's to run. A thread has finished once its handle is signaled.
THREAD_BLOCK        equ 1024            ; throw.asm's PHI_THREAD_SIZE
TLS_SLOTS           equ 64              ; the ones in the TEB itself
THREADS_MOST        equ 64              ; user.threads' MOST

; eax = a block of THREAD_BLOCK bytes: the running thread's from now on (no catches yet);
; keeps the other registers
phi_thread_setup:
    mov [eax], eax                      ; PHI_THREAD_SELF: its own address
    mov dword [eax + 4], 0              ; PHI_THREAD_CATCH
    mov dword [eax + 8], 0              ; PHI_THREAD_UNWIND
    push ecx
    push edx
    push eax
    cmp dword [phi_tls_index], -1
    jne .set
    call [phi_imp_TlsAlloc]
    cmp eax, TLS_SLOTS
    jae .no_slot
    mov [phi_tls_index], eax
.set:
    mov eax, [esp]
    push eax
    push dword [phi_tls_index]
    call [phi_imp_TlsSetValue]
    pop eax
    pop edx
    pop ecx
    ret
.no_slot:
    mov esi, .none
    call phi_print
    mov ebx, -1
    jmp phi_exit
.none: db 13, 10, 'PHI: no TLS slot for the program', 13, 10, 0

; -> eax = the running thread's block; keeps the other registers
phi_thread_block:
    mov eax, [phi_tls_index]
    mov eax, [fs:0xE10 + eax * 4]
    ret

phi_tls_index: dd -1                    ; the program's TLS slot

; the program's first thread gets its block before any of its code runs
phi_threads_init:
    mov eax, phi_main_thread
    jmp phi_thread_setup

align 4, db 0
phi_main_thread: times 1024 db 0        ; the block of the program's first thread

; args: method value stackBottom stackTop -> the thread's number (1 to THREADS_MOST - 1),
; or -1
OS_ThreadStart:
    push ebx
    push esi
    push edi
    mov ecx, 1
.find:
    cmp byte [phi_thread_used + ecx], 0
    je .found
    inc ecx
    cmp ecx, THREADS_MOST
    jb .find
    mov eax, -1
    jmp .out
.found:
    mov byte [phi_thread_used + ecx], 1
    push ecx
    mov edx, [esp + 32]                 ; the top of what it's given: its block, and below it
    sub edx, THREAD_BLOCK + 8           ; its method and value
    mov eax, [esp + 20]
    mov [edx], eax
    mov eax, [esp + 24]
    mov [edx + 4], eax
    push 0                              ; (no thread id wanted)
    push 0                              ; it starts at once
    push edx                            ; what phi_host_thread_main is given
    push phi_host_thread_main
    push 0                              ; Windows' usual stack size
    push 0
    call [phi_imp_CreateThread]
    pop ecx
    test eax, eax
    jz .failed
    mov [phi_thread_handles + ecx * 4], eax
    mov eax, ecx
    jmp .out
.failed:
    mov byte [phi_thread_used + ecx], 0
    mov eax, -1
.out:
    pop edi
    pop esi
    pop ebx
    ret

; where a new thread starts (Windows calls it, stdcall): [esp + 4] = its method and value,
; then its block; when the method returns, the thread ends
phi_host_thread_main:
    push ebx
    push esi
    push edi
    push ebp
    mov ebx, [esp + 20]
    lea eax, [ebx + 8]
    call phi_thread_setup
    mov ebp, esp
    push dword [ebx + 4]
    call [ebx]
    mov esp, ebp
    pop ebp
    pop edi
    pop esi
    pop ebx
    xor eax, eax
    ret 4

; arg: thread -> 1 once it has finished, 0 while it runs, -1 if it isn't one
OS_ThreadDone:
    mov ecx, [esp + 4]
    cmp ecx, THREADS_MOST
    jae .not
    cmp byte [phi_thread_used + ecx], 0
    je .not
    push 0                              ; (don't wait)
    push dword [phi_thread_handles + ecx * 4]
    call [phi_imp_WaitForSingleObject]
    test eax, eax                       ; WAIT_OBJECT_0: it has finished
    sete al
    movzx eax, al
    ret
.not:
    mov eax, -1
    ret

; arg: thread -> 1 (its number given back), or 0 if it hasn't finished
OS_ThreadFree:
    push dword [esp + 4]
    call OS_ThreadDone
    add esp, 4
    cmp eax, 1
    jne .no
    mov ecx, [esp + 4]
    push ecx
    push dword [phi_thread_handles + ecx * 4]
    call [phi_imp_CloseHandle]
    pop ecx
    mov dword [phi_thread_handles + ecx * 4], 0
    mov byte [phi_thread_used + ecx], 0
    mov eax, 1
    ret
.no:
    xor eax, eax
    ret

align 4, db 0
phi_thread_handles: times THREADS_MOST dd 0  ; each thread's handle
phi_thread_used:    times THREADS_MOST db 0  ; 1 for a thread number that's taken
