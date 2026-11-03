; provides: phi_thread_setup phi_thread_block phi_tls_index
; requires: phi_imports phi_print phi_exit
;
; Each thread's block in a Windows program (throw.asm's PHI_THREAD_...: its catches and
; the error it throws). Windows keeps fs (and gs) for itself, so the block's address is in
; a TLS slot of the thread's TEB: TlsSlots, at fs:0xE10, for the first 64 slots, which the
; code generator reads straight from there (phi_tls_index says which).
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
