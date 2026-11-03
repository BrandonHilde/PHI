; provides: phi_log_take phi_log_give phi_log_held phi_log_back
; requires: phi_thread_block
;
; A whole log statement at a time, across a program's threads: the code generator puts a
; call to phi_log_take before each log (and debug) in a program, and phi_log_give after it
; (and after the line is flushed), so two threads' lines never mix. (PHI OS programs,
; and Linux and Windows ones, which share this file.)
;
; The lock knows which thread has it (its block's address, phi_thread_block), so a log
; whose values call a method that logs too doesn't wait for itself. A catch keeps how deep
; its thread was in the lock (phi_log_held), and a throw that comes back to it puts that
; back (phi_log_back), so an error in a log's value doesn't keep the lock. Each changes
; eax, ecx and edx.

phi_log_take:
    call phi_thread_block
    mov ecx, eax                    ; this thread
    cmp [phi_log_owner], ecx
    je .nested
.try:
    xor eax, eax
    lock cmpxchg [phi_log_owner], ecx
    jz .nested
.wait:
    pause                           ; another thread is logging
    cmp dword [phi_log_owner], 0
    jne .wait
    jmp .try
.nested:
    inc dword [phi_log_depth]       ; (only the thread that has it changes this)
    ret

phi_log_give:
    cmp dword [phi_log_depth], 0
    je .done
    dec dword [phi_log_depth]
    jnz .done                       ; a log inside a log: the outer one gives it back
    mov dword [phi_log_owner], 0
.done:
    ret

; -> eax = how many logs deep this thread is in the lock (0 if it hasn't got it)
phi_log_held:
    call phi_thread_block
    cmp [phi_log_owner], eax
    mov eax, 0
    jne .done
    mov eax, [phi_log_depth]
.done:
    ret

; eax = how deep this thread was when a catch was set: the throw coming back to it gives
; back the logs it left (the one whose value threw, and any inside it)
phi_log_back:
    mov edx, eax
    call phi_thread_block
    cmp [phi_log_owner], eax
    jne .done
    mov [phi_log_depth], edx
    test edx, edx
    jnz .done
    mov dword [phi_log_owner], 0
.done:
    ret

phi_log_owner: dd 0
phi_log_depth: dd 0
