; provides: phi_thread_setup phi_thread_block phi_tls_index
; requires: phi_imports phi_print phi_exit
;
; Each thread's block in a 64-bit Windows program (throw.asm's PHI_THREAD_...), as in a
; 32-bit one (x86_32_windows/threads.asm): its address is in a TLS slot of the thread's
; TEB, whose first 64 are at gs:0x1480 here, which the code generator reads straight
; from there (phi_tls_index says which).
TLS_SLOTS           equ 64              ; the ones in the TEB itself

; (the same as system.asm's: whichever comes first defines it)
%ifnmacro PHI_CALL 1-4
%macro PHI_CALL 1-4
    mov r12, rsp
    and rsp, -16
    sub rsp, 64
%if %0 >= 2
    mov qword [rsp + 32], %2
%endif
%if %0 >= 3
    mov qword [rsp + 40], %3
%endif
%if %0 >= 4
    mov qword [rsp + 48], %4
%endif
    call [rel phi_imp_%1]
    mov rsp, r12
%endmacro
%endif

; rax = a block of PHI_THREAD_SIZE bytes: the running thread's from now on (no catches
; yet); keeps the other registers
phi_thread_setup:
    mov [rax], rax                      ; PHI_THREAD_SELF: its own address
    mov qword [rax + 8], 0              ; PHI_THREAD_CATCH
    mov qword [rax + 16], 0             ; PHI_THREAD_UNWIND
    push rax
    push rcx
    push rdx
    push r8
    push r9
    push r10
    push r11
    push r12
    cmp dword [rel phi_tls_index], -1
    jne .set
    PHI_CALL TlsAlloc
    cmp eax, TLS_SLOTS
    jae .no_slot
    mov [rel phi_tls_index], eax
.set:
    mov ecx, [rel phi_tls_index]
    mov rdx, [rsp + 56]                 ; the block
    PHI_CALL TlsSetValue
    pop r12
    pop r11
    pop r10
    pop r9
    pop r8
    pop rdx
    pop rcx
    pop rax
    ret
.no_slot:
    lea rsi, [rel .none]
    call phi_print
    mov ebx, -1
    jmp phi_exit
.none: db 13, 10, 'PHI: no TLS slot for the program', 13, 10, 0

; -> rax = the running thread's block; keeps the other registers
phi_thread_block:
    mov eax, [rel phi_tls_index]
    mov rax, [gs:0x1480 + rax * 8]
    ret

phi_tls_index: dd -1                    ; the program's TLS slot
