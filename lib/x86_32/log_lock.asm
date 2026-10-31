; provides: phi_log_take phi_log_give
; requires: phi_cores_running
;
; A whole log statement at a time, across cores: the code generator puts a call to
; phi_log_take before each log (and debug) in a kernel, and phi_log_give after it, so two
; cores' lines never mix. Until proc.cores has started other cores (phi_cores_running),
; both do nothing.
;
; The lock knows which core has it (its local APIC ID), so a log whose values call a
; method that logs too doesn't wait for itself. While a core has it, that core's
; interrupts are off, so an interrupt handler that logs can't wait for the code it
; interrupted. Changes eax, ecx and edx.
APIC_ID equ 0xFEE00020

phi_log_take:
    cmp dword [phi_cores_running], 0
    je .done
    pushfd
    pop edx                         ; the flags as they were
    cli
    mov ecx, [APIC_ID]
    shr ecx, 24
    inc ecx                         ; this core: its APIC ID + 1 (never 0, which is free)
    cmp [phi_log_owner], ecx
    je .nested
.try:
    xor eax, eax
    lock cmpxchg [phi_log_owner], ecx
    jz .taken
    push edx
    popfd                           ; another core is logging: interrupts as they were while waiting
.wait:
    pause
    cmp dword [phi_log_owner], 0
    jne .wait
    cli
    jmp .try
.taken:
    mov [phi_log_flags], edx        ; (only the core that has it writes these)
.nested:
    inc dword [phi_log_depth]
.done:
    ret

phi_log_give:
    cmp dword [phi_log_depth], 0    ; (nothing taken: one core, or cores started mid-log)
    je .done
    dec dword [phi_log_depth]
    jnz .done                       ; a log inside a log: the outer one gives it back
    push dword [phi_log_flags]
    mov dword [phi_log_owner], 0
    popfd                           ; interrupts as they were before the log
.done:
    ret

phi_log_owner: dd 0
phi_log_depth: dd 0
phi_log_flags: dd 0
