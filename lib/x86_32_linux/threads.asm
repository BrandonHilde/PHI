; provides: phi_thread_setup phi_thread_block phi_main_thread OS_ThreadStart OS_ThreadDone OS_ThreadFree
; init: phi_threads_init
;
; A Linux program's threads (user.threads) are Linux threads made with clone, sharing the
; program's memory, files and signal handlers, on the stack the program gives each (its
; block at the top). Linux writes the thread's id in its slot when it starts and clears it
; when the thread has gone (CLONE_CHILD_CLEARTID), so a slot that's 0 again is a thread
; that has finished and no longer uses its stack. A thread ends with exit (only itself);
; the program's exit is exit_group, which ends them all.
;
; Each thread's block in a Linux program (throw.asm's PHI_THREAD_...: its catches and the
; error it throws), reached through gs: a thread-local segment Linux makes with
; set_thread_area (one entry of its GDT, the same for every thread, whose base Linux
; changes as it switches threads), as a C library does for its own thread data.
SYS_SET_THREAD_AREA equ 243
SYS_THREAD_EXIT     equ 1               ; exit: this thread only
SYS_CLONE           equ 120
CLONE_THREAD_FLAGS  equ 0x350F00        ; VM, FS, FILES, SIGHAND, THREAD, SYSVSEM, PARENT_SETTID, CHILD_CLEARTID
THREADS_MOST        equ 64              ; user.threads' MOST
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
    mov edx, [esp + 32]                 ; the top of its stack: its block, and below it its
    sub edx, THREAD_BLOCK + 8           ; method and value
    mov eax, [esp + 20]
    mov [edx], eax
    mov eax, [esp + 24]
    mov [edx + 4], eax
    lea edi, [phi_thread_ids + ecx * 4] ; Linux sets it, and clears it when the thread has gone
    mov dword [edi], -1
    mov eax, SYS_CLONE
    mov ebx, CLONE_THREAD_FLAGS
    mov ecx, edx
    mov edx, edi
    xor esi, esi
    int 0x80
    test eax, eax
    jz phi_host_thread_main             ; (the new thread, on its own stack)
    pop ecx
    jns .started
    mov byte [phi_thread_used + ecx], 0
    mov dword [phi_thread_ids + ecx * 4], 0
    mov eax, -1
    jmp .out
.started:
    mov eax, ecx
.out:
    pop edi
    pop esi
    pop ebx
    ret

; where a new thread starts: [esp] = its method, [esp + 4] = its value, and above them
; its block; when the method returns, the thread ends
phi_host_thread_main:
    lea eax, [esp + 8]
    call phi_thread_setup
    pop eax
    call eax
    mov eax, SYS_THREAD_EXIT
    xor ebx, ebx
    int 0x80
.never:
    jmp .never

; arg: thread -> 1 once it has finished, 0 while it runs, -1 if it isn't one
OS_ThreadDone:
    mov ecx, [esp + 4]
    cmp ecx, THREADS_MOST
    jae .not
    cmp byte [phi_thread_used + ecx], 0
    je .not
    xor eax, eax
    cmp dword [phi_thread_ids + ecx * 4], 0
    sete al
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
    mov byte [phi_thread_used + ecx], 0
    ret
.no:
    xor eax, eax
    ret

align 4, db 0
phi_thread_ids:  times THREADS_MOST dd 0    ; each thread's Linux id while it runs, 0 once it has gone
phi_thread_used: times THREADS_MOST db 0    ; 1 for a thread number that's taken
