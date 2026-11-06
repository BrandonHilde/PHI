; provides: phi_thread_setup phi_thread_block phi_tls_index phi_main_thread OS_ThreadEntry
; init: phi_threads_init
; requires: phi_imports phi_print phi_exit phi_stack_guarantee
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

; -> where a new thread starts, for CreateThread
OS_ThreadEntry:
    mov eax, phi_host_thread_main
    ret

; where a new thread starts (Windows calls it, stdcall): [esp + 4] = its method and value,
; then its block; when the method returns, the thread ends. The first of its stack is its
; spare stack (for the stack running out: system.asm's phi_fault_register).
phi_host_thread_main:
    push ebx
    push esi
    push edi
    push ebp
    mov ebx, [esp + 20]
    lea eax, [ebx + 8]
    call phi_thread_setup
    mov [eax + THREAD_SPARE], esp
    mov ebp, esp
    sub esp, SPARE_STACK
    call phi_stack_guarantee
    push dword [ebx + 4]
    call [ebx]
    mov esp, ebp
    pop ebp
    pop edi
    pop esi
    pop ebx
    xor eax, eax
    ret 4
