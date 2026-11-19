; provides: phi_foreign_thread
; requires: phi_thread_setup phi_tls_index phi_threads_many phi_stack_guarantee phi_exit phi_imports
;
; Where outside code comes into the program (a foreign entry: C calling a PHI method) it
; may be on a thread that isn't one of the program's: a library's own, as one that plays
; sound has. A thread with no block is given one here the first time, with its spare
; stack, which it keeps for as long as it lives. Keeps every register but rax, r10 and r11
; (the ones C's values come in are still to be read).
phi_foreign_thread:
    mov eax, [rel phi_tls_index]
    cmp qword [gs:0x1480 + rax * 8], 0
    je .new
    ret
.new:
    push rcx
    push rdx
    push r8
    push r9
    push rbp
    mov rbp, rsp
    sub rsp, 64
    and rsp, -16
    movdqu [rsp], xmm0
    movdqu [rsp + 16], xmm1
    movdqu [rsp + 32], xmm2
    movdqu [rsp + 48], xmm3
    xor ecx, ecx
    mov edx, SPARE_STACK + THREAD_BLOCK
    mov r8d, 0x3000                     ; MEM_COMMIT | MEM_RESERVE
    mov r9d, 4                          ; PAGE_READWRITE
    sub rsp, 32
    call [rel phi_imp_VirtualAlloc]
    add rsp, 32
    test rax, rax
    jz .none
    mov dword [rel phi_threads_many], 1
    add rax, SPARE_STACK                ; its block, above its spare stack
    call phi_thread_setup
    mov [rax + THREAD_SPARE], rax
    call phi_stack_guarantee
    movdqu xmm0, [rsp]
    movdqu xmm1, [rsp + 16]
    movdqu xmm2, [rsp + 32]
    movdqu xmm3, [rsp + 48]
    mov rsp, rbp
    pop rbp
    pop r9
    pop r8
    pop rdx
    pop rcx
    ret
.none:
    mov ebx, -1
    jmp phi_exit
