; provides: phi_thread_setup phi_thread_block
;
; Each thread's block in a Linux program (throw.asm's PHI_THREAD_...: its catches and the
; error it throws), reached through gs: a thread-local segment Linux makes with
; set_thread_area (one entry of its GDT, the same for every thread, whose base Linux
; changes as it switches threads), as a C library does for its own thread data.
SYS_SET_THREAD_AREA equ 243
THREAD_BLOCK        equ 1024            ; throw.asm's PHI_THREAD_SIZE

; eax = a block of THREAD_BLOCK bytes: the running thread's from now on (no catches yet),
; reached through gs; keeps the other registers
phi_thread_setup:
    mov [eax], eax                      ; PHI_THREAD_SELF: its own address
    mov dword [eax + 4], 0              ; PHI_THREAD_CATCH
    mov dword [eax + 8], 0              ; PHI_THREAD_UNWIND
    push ebx
    push ecx
    push eax
    sub esp, 16                         ; a user_desc: the entry (-1 the first time: Linux
    mov ecx, [phi_tls_entry]            ; picks one), base, limit and its flags
    mov [esp], ecx
    mov [esp + 4], eax
    mov dword [esp + 8], THREAD_BLOCK - 1
    mov dword [esp + 12], 0x41          ; 32-bit, usable, the limit in bytes
    mov eax, SYS_SET_THREAD_AREA
    mov ebx, esp
    int 0x80
    test eax, eax
    jnz .done
    mov ecx, [esp]                      ; the entry it's in
    mov [phi_tls_entry], ecx
    lea ecx, [ecx * 8 + 3]
    mov gs, cx
.done:
    add esp, 16
    pop eax
    pop ecx
    pop ebx
    ret

; -> eax = the running thread's block; keeps the other registers
phi_thread_block:
    mov eax, [gs:0]
    ret

phi_tls_entry: dd -1                    ; the GDT entry Linux gave the program's threads
