; provides: phi_log_take phi_log_give phi_log_held phi_log_back
; requires: phi_thread_block
;
; A whole log statement at a time, across a 64-bit program's threads, as in 32-bit ones
; (x86_32_user/log_lock.asm): the thread that has the lock is told by its block's
; address, so a log inside a log doesn't wait for itself; and a throw back to a catch
; puts the lock as it was at the catch (phi_log_held, phi_log_back). Changes rax, rcx and
; rdx.

phi_log_take:
    call phi_thread_block
    mov rcx, rax                    ; this thread
    cmp [rel phi_log_owner], rcx
    je .nested
.try:
    xor eax, eax
    lock cmpxchg [rel phi_log_owner], rcx
    jz .nested
.wait:
    pause                           ; another thread is logging
    cmp qword [rel phi_log_owner], 0
    jne .wait
    jmp .try
.nested:
    inc dword [rel phi_log_depth]   ; (only the thread that has it changes this)
    ret

phi_log_give:
    cmp dword [rel phi_log_depth], 0
    je .done
    dec dword [rel phi_log_depth]
    jnz .done                       ; a log inside a log: the outer one gives it back
    mov qword [rel phi_log_owner], 0
.done:
    ret

; -> rax = how many logs deep this thread is in the lock (0 if it hasn't got it)
phi_log_held:
    call phi_thread_block
    cmp [rel phi_log_owner], rax
    mov eax, 0
    jne .done
    mov eax, [rel phi_log_depth]
.done:
    ret

; rax = how deep this thread was when a catch was set: the throw coming back to it gives
; back the logs it left
phi_log_back:
    mov rdx, rax
    call phi_thread_block
    cmp [rel phi_log_owner], rax
    jne .done
    mov [rel phi_log_depth], edx
    test edx, edx
    jnz .done
    mov qword [rel phi_log_owner], 0
.done:
    ret

align 8, db 0
phi_log_owner: dq 0
phi_log_depth: dd 0
