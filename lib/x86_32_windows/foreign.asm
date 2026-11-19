; provides: phi_foreign_thread
; requires: phi_thread_setup phi_tls_index phi_threads_many phi_stack_guarantee phi_exit phi_imports
;
; Where outside code comes into the program (a foreign entry: C calling a PHI method) it
; may be on a thread that isn't one of the program's: a library's own, as one that plays
; sound has. A thread with no block is given one here the first time, with its spare
; stack, which it keeps for as long as it lives. Keeps every register but eax, ecx and edx.
phi_foreign_thread:
    mov eax, [phi_tls_index]
    cmp dword [fs:0xE10 + eax * 4], 0
    je .new
    ret
.new:
    push 4                              ; PAGE_READWRITE
    push 0x3000                         ; MEM_COMMIT | MEM_RESERVE
    push SPARE_STACK + THREAD_BLOCK
    push 0
    call [phi_imp_VirtualAlloc]
    test eax, eax
    jz .none
    mov dword [phi_threads_many], 1
    add eax, SPARE_STACK                ; its block, above its spare stack
    call phi_thread_setup
    mov [eax + THREAD_SPARE], eax
    jmp phi_stack_guarantee
.none:
    mov ebx, -1
    jmp phi_exit
