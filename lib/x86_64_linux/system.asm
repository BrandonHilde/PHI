; provides: phi_exit phi_fault_register phi_stack_restore phi_spare_stack phi_read_byte OS_ReadKey Bootloader_WaitForKeyPress phi_read_char OS_KeyAvailable OS_CurrentTask OS_Arguments OS_Grow OS_FreeMemory OS_TotalMemory OS_ClearScreen
; requires: phi_flush phi_write_all phi_thread_block phi_main_thread
; init: phi_linux_start
;
; What a PHI OS program asks its kernel for, answered by 64-bit Linux system calls (the
; syscall instruction: a number in rax, arguments in rdi, rsi, rdx, r10, r8, r9; rcx and
; r11 change, every other register is kept), as x86_32_linux's are by the 32-bit ones.
;
; Arguments PHI pushes are 8 bytes each in 64-bit code: the first is at [rsp + 8]. Each
; call keeps rsi and rdi.
SYS_READ       equ 0
SYS_POLL       equ 7
SYS_BRK        equ 12
SYS_GETPID     equ 39
SYS_SYSINFO    equ 99
SYS_EXIT_GROUP equ 231

ARGUMENTS_SIZE equ 1024

; Called before the program's code, while the stack is as Linux left it: [rsp + 8] is how
; many arguments there are, then their addresses, the program's own name first. Keeps its
; arguments joined by spaces. (Time is lib/phi/host/time_linux*.phi's, in PHI.)
phi_linux_start:
    mov ecx, [rsp + 8]
    lea rsi, [rsp + 24]
    lea rdi, [rel phi_arguments]
    lea rdx, [rel phi_arguments + ARGUMENTS_SIZE - 1]
    dec ecx
    jle .done
.argument:
    mov rbx, [rsi]
.copy:
    mov al, [rbx]
    test al, al
    jz .next
    cmp rdi, rdx
    jae .done
    mov [rdi], al
    inc rdi
    inc rbx
    jmp .copy
.next:
    add rsi, 8
    dec ecx
    jz .done
    cmp rdi, rdx
    jae .done
    mov byte [rdi], ' '
    inc rdi
    jmp .argument
.done:
    mov byte [rdi], 0
    ret

; ebx = exit code
phi_exit:
    call phi_flush
    mov edi, ebx
    mov eax, SYS_EXIT_GROUP
    syscall

; eax = the next byte of standard input, or -1 at its end
phi_read_byte:
    push rsi
    push rdi
    mov eax, SYS_READ
    xor edi, edi
    lea rsi, [rel phi_byte]
    mov edx, 1
    syscall
    pop rdi
    pop rsi
    cmp rax, 1
    jne .end
    movzx eax, byte [rel phi_byte]
    ret
.end:
    mov eax, -1
    ret

; eax = the next character typed; Enter is 13, as on PHI OS (a line ending in 10, or in
; 13 10, gives one), and 0 means the input ended. A terminal hands a program its input a
; line at a time, so this waits for Enter.
OS_ReadKey:
Bootloader_WaitForKeyPress:
phi_read_char:
    call phi_flush
.again:
    call phi_read_byte
    mov dl, [rel phi_last_byte]
    mov [rel phi_last_byte], al
    cmp eax, 10
    jne .not_enter
    cmp dl, 13
    je .again                           ; the end of 13 10: that Enter was given already
    mov eax, 13
.not_enter:
    test eax, eax
    jns .done
    xor eax, eax
.done:
    ret

; eax = 1 if there's input waiting
OS_KeyAvailable:
    push rsi
    push rdi
    mov rax, 0x100000000                ; a pollfd: standard input, anything to read? (on the stack)
    push rax
    mov eax, SYS_POLL
    mov rdi, rsp
    mov esi, 1
    xor edx, edx                        ; don't wait
    syscall
    add rsp, 8
    pop rdi
    pop rsi
    cmp rax, 1
    sete al
    movzx eax, al
    ret

; -> this program's process number
OS_CurrentTask:
    mov eax, SYS_GETPID
    syscall
    ret

; -> what came after the program's name on its command line
OS_Arguments:
    lea rax, [rel phi_arguments]
    ret

; arg: bytes -> rax = where the new memory starts (right after what the heap has), or 0.
; The heap grows by moving the program's break, a page at a time.
OS_Grow:
    push rdi
    push rsi
    mov rax, [rel phi_heap_end]
    test rax, rax
    jnz .grow
    mov eax, SYS_BRK
    xor edi, edi                        ; where the break is now
    syscall
    add rax, 4095
    and rax, ~4095
    mov [rel phi_heap_end], rax
.grow:
    mov edx, [rsp + 24]                 ; (after the two pushes and the return address)
    add rdx, 4095
    and rdx, ~4095
    add rdx, rax                        ; the new end
    push rax
    push rdx
    mov eax, SYS_BRK
    mov rdi, rdx
    syscall
    pop rdx
    pop rcx
    cmp rax, rdx
    jne .failed                         ; Linux didn't move it
    mov [rel phi_heap_end], rdx
    mov rax, rcx
    pop rsi
    pop rdi
    ret
.failed:
    xor eax, eax
    pop rsi
    pop rdi
    ret

; -> all memory, in KB
OS_TotalMemory:
    mov edx, 32                         ; sysinfo's totalram
    jmp phi_memory

; -> free memory, in KB
OS_FreeMemory:
    mov edx, 40                         ; sysinfo's freeram

phi_memory:
    push rdi
    push rdx
    sub rsp, 128                        ; (Linux's sysinfo, on the stack)
    mov eax, SYS_SYSINFO
    mov rdi, rsp
    syscall
    mov rdx, [rsp + 128]
    mov rax, [rsp + rdx]
    mov ecx, [rsp + 104]                ; in units of mem_unit bytes
    mul rcx
    shr rax, 10                         ; (a u32 of KB: up to 4 TB)
    add rsp, 128
    pop rdx
    pop rdi
    ret

; clear the terminal and put the cursor at its top left
OS_ClearScreen:
    call phi_flush
    push rsi
    push rdi
    mov edi, 1                          ; standard output
    lea rsi, [rel phi_clear]
    mov edx, phi_clear_end - phi_clear
    call phi_write_all
    pop rdi
    pop rsi
    ret

phi_clear: db 27, '[2J', 27, '[H'
phi_clear_end:
phi_byte: db 0
phi_last_byte: db 0
align 4
align 8
phi_heap_end: dq 0                      ; the break: where the heap ends
phi_arguments: times ARGUMENTS_SIZE db 0

; rax = where this program goes when the CPU stops it (dividing by zero, or memory that
; isn't there): throw.asm's phi_fault_entry, with ebx = the kind of error. Linux sends a
; signal (SIGFPE, SIGSEGV); its handler changes the stopped code's registers in the
; signal's saved context, so when the handler returns (rt_sigreturn) it carries on there,
; as PHI OS sends a program. A stack that has run out has no room for the handler, or for
; the throw after it, so the handler runs on a spare stack (each thread has its own:
; threads.asm's), and the throw carries on there
phi_fault_register:
    push rdi
    push rsi
    push r8
    push r9
    mov [rel phi_fault_to], rax
    xor edi, edi
    mov esi, SPARE_STACK
    mov edx, 3                          ; read and write
    mov r10d, 0x22                      ; private, not from a file
    mov r8, -1
    xor r9d, r9d
    mov eax, SYS_MMAP
    syscall
    cmp rax, -4096
    ja .no_spare
    lea rcx, [rax + SPARE_STACK]
    mov [rel phi_main_thread + THREAD_SPARE], rcx
    call phi_spare_stack
.no_spare:
    mov edi, SIGFPE
    call .handle
    mov edi, SIGSEGV
    call .handle
    pop r9
    pop r8
    pop rsi
    pop rdi
    ret
.handle:
    mov eax, SYS_RT_SIGACTION
    lea rsi, [rel phi_fault_action]
    xor edx, edx                        ; (the old one isn't wanted)
    mov r10d, 8                         ; the size of a signal mask
    syscall
    ret

; rax = the bottom of SPARE_STACK bytes: where this thread's signal handler runs
phi_spare_stack:
    push rdi
    push rsi
    sub rsp, 24                         ; Linux's stack_t: where, flags, how big
    mov [rsp], rax
    mov qword [rsp + 8], 0
    mov qword [rsp + 16], SPARE_STACK
    mov rdi, rsp
    xor esi, esi                        ; (the old one isn't wanted)
    mov eax, SYS_SIGALTSTACK
    syscall
    add rsp, 24
    pop rsi
    pop rdi
    ret

; after a throw of the stack running out, on its way back to the catch: nothing to do here,
; as what's past the end of the stack stays there to be stopped by
phi_stack_restore:
    ret

; the signal handler: edi = the signal, rsi its siginfo (the address at +16), rdx the
; ucontext (the registers from +40 on)
phi_fault_handler:
    mov eax, ERROR_DIVIDE
    cmp edi, SIGFPE
    je .send
    ; the stack has run out if the memory is just by the stack pointer. On the spare stack
    ; itself there's nowhere further to go.
    mov rcx, [rsi + 16]
    mov r8, [rdx + UCONTEXT_RSP]
    lea r9, [r8 + 4096]
    cmp rcx, r9
    ja .memory
    lea r9, [r8 - 65536]
    cmp rcx, r9
    jb .memory
    call phi_thread_block
    mov rax, [rax + THREAD_SPARE]
    test rax, rax
    jz .again
    lea r9, [rax - SPARE_STACK]
    cmp r8, r9
    jb .to_spare
    cmp r8, rax
    jbe .again
.to_spare:
    mov [rdx + UCONTEXT_RSP], rax
    mov eax, ERROR_STACK
    jmp .send
.memory:
    mov eax, ERROR_ACCESS
    cmp qword [rsi + 16], 4096
    jae .send
    mov eax, ERROR_EMPTY
.send:
    mov rcx, [rel phi_fault_to]
    cmp [rdx + UCONTEXT_RIP], rcx
    je .again                           ; the entry itself was stopped: it would only be again
    mov [rdx + UCONTEXT_RBX], rax
    mov rax, [rdx + UCONTEXT_RIP]
    mov [rdx + UCONTEXT_RCX], rax       ; where it happened, for its line
    mov [rdx + UCONTEXT_RIP], rcx
    ret
.again:
    mov eax, SYS_EXIT_GROUP
    mov edi, -1
    syscall

; where a signal handler returns to: the registers back from the context, as it changed them
phi_fault_return:
    mov eax, SYS_RT_SIGRETURN
    syscall

SYS_RT_SIGACTION equ 13
SYS_RT_SIGRETURN equ 15
SYS_MMAP         equ 9
SYS_SIGALTSTACK  equ 131
SPARE_STACK      equ 16384
THREAD_SPARE     equ 1016               ; throw.asm's PHI_THREAD_SPARE
SIGFPE           equ 8
SIGSEGV          equ 11
SA_SIGINFO       equ 4
SA_RESTORER      equ 0x04000000
SA_ONSTACK       equ 0x08000000
ERROR_DIVIDE     equ 3                  ; ErrorKind (lib/phi/lang/error.phi)
ERROR_EMPTY      equ 4
ERROR_ACCESS     equ 5
ERROR_STACK      equ 8
UCONTEXT_RBX     equ 40 + 11 * 8        ; in a ucontext: its registers' rbx and rip
UCONTEXT_RCX     equ 40 + 14 * 8
UCONTEXT_RSP     equ 40 + 15 * 8
UCONTEXT_RIP     equ 40 + 16 * 8
align 8
phi_fault_to: dq 0
phi_fault_action:                       ; the kernel's sigaction: handler, flags, restorer, mask
    dq phi_fault_handler, SA_SIGINFO | SA_RESTORER | SA_ONSTACK, phi_fault_return, 0
