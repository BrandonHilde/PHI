; provides: phi_exit phi_fault_register phi_stack_restore phi_spare_stack phi_read_byte OS_ReadKey Bootloader_WaitForKeyPress phi_read_char OS_KeyAvailable OS_CurrentTask OS_Arguments OS_Grow OS_FreeMemory OS_TotalMemory OS_ClearScreen
; requires: phi_flush phi_write_all phi_thread_block phi_main_thread
; init: phi_linux_start
;
; What a PHI OS program asks its kernel for, answered by Linux system calls (int 0x80,
; the 32-bit ones: a number in eax, arguments in ebx, ecx, edx, esi, edi; every register
; but eax is kept). The calls PHI OS has that Linux has no answer for (the screen's
; cells, other PHI OS programs, sound, windows) aren't here, so using one is an error
; when the program is built. Time, files and the network are PHI now, in lib/phi/host/.
SYS_EXIT_GROUP equ 252
SYS_READ      equ 3
SYS_GETPID    equ 20
SYS_BRK       equ 45
SYS_SYSINFO   equ 116
SYS_POLL      equ 168

ARGUMENTS_SIZE equ 1024

; Called before the program's code, while the stack is as Linux left it: [esp + 4] is how
; many arguments there are, then their addresses, the program's own name first. Keeps its
; arguments joined by spaces. (Time is lib/phi/host/time_linux*.phi's, in PHI.)
phi_linux_start:
    mov ecx, [esp + 4]
    lea esi, [esp + 12]
    mov edi, phi_arguments
    dec ecx
    jle .done
.argument:
    mov ebx, [esi]
.copy:
    mov al, [ebx]
    test al, al
    jz .next
    cmp edi, phi_arguments + ARGUMENTS_SIZE - 1
    jae .done
    mov [edi], al
    inc edi
    inc ebx
    jmp .copy
.next:
    add esi, 4
    dec ecx
    jz .done
    cmp edi, phi_arguments + ARGUMENTS_SIZE - 1
    jae .done
    mov byte [edi], ' '
    inc edi
    jmp .argument
.done:
    mov byte [edi], 0
    ret

; ebx = exit code
phi_exit:
    call phi_flush
    mov eax, SYS_EXIT_GROUP
    int 0x80

; eax = the next byte of standard input, or -1 at its end
phi_read_byte:
    mov eax, SYS_READ
    xor ebx, ebx
    mov ecx, phi_byte
    mov edx, 1
    int 0x80
    cmp eax, 1
    jne .end
    movzx eax, byte [phi_byte]
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
    mov dl, [phi_last_byte]
    mov [phi_last_byte], al
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
    push dword 1                        ; anything to read?
    push dword 0                        ; standard input
    mov eax, SYS_POLL
    mov ebx, esp
    mov ecx, 1
    xor edx, edx                        ; don't wait
    int 0x80
    add esp, 8
    cmp eax, 1
    sete al
    movzx eax, al
    ret

; -> this program's process number
OS_CurrentTask:
    mov eax, SYS_GETPID
    int 0x80
    ret

; -> what came after the program's name on its command line
OS_Arguments:
    mov eax, phi_arguments
    ret

; arg: bytes -> where the new memory starts (whole pages, zeroed, right after the last
; it gave), or 0. The heap is the program's break, which Linux moves with brk.
OS_Grow:
    mov eax, [phi_heap_end]
    test eax, eax
    jnz .grow
    mov eax, SYS_BRK
    xor ebx, ebx                        ; where the break is now
    int 0x80
    add eax, 4095
    and eax, ~4095
    mov [phi_heap_end], eax
.grow:
    mov edx, [esp + 4]
    add edx, 4095
    jc .failed
    and edx, ~4095
    add edx, eax                        ; the new end
    jc .failed
    push eax
    mov eax, SYS_BRK
    mov ebx, edx
    int 0x80
    pop ecx
    cmp eax, edx
    jne .failed                         ; Linux didn't move it
    mov [phi_heap_end], edx
    mov eax, ecx
    ret
.failed:
    xor eax, eax
    ret

; -> all memory, in KB
OS_TotalMemory:
    mov edx, 16                         ; sysinfo's totalram
    jmp phi_memory

; -> free memory, in KB
OS_FreeMemory:
    mov edx, 20                         ; sysinfo's freeram

phi_memory:
    sub esp, 64                         ; (Linux's sysinfo, on the stack)
    push edx
    mov eax, SYS_SYSINFO
    lea ebx, [esp + 4]
    int 0x80
    pop edx
    mov eax, [esp + edx]
    mul dword [esp + 52]                ; in units of mem_unit bytes
    shrd eax, edx, 10
    add esp, 64
    ret

; clear the terminal and put the cursor at its top left
OS_ClearScreen:
    call phi_flush
    mov ebx, 1                          ; standard output
    mov ecx, phi_clear
    mov edx, phi_clear_end - phi_clear
    jmp phi_write_all

phi_clear: db 27, '[2J', 27, '[H'
phi_clear_end:
phi_byte: db 0
phi_last_byte: db 0
align 4
phi_heap_end: dd 0
phi_arguments: times ARGUMENTS_SIZE db 0

; eax = where this program goes when the CPU stops it (dividing by zero, or memory that
; isn't there): throw.asm's phi_fault_entry, with ebx = the kind of error. Linux sends a
; signal (SIGFPE, SIGSEGV); its handler changes the stopped code's registers in the
; signal's saved context, so when the handler returns (rt_sigreturn) it carries on there,
; as PHI OS sends a program. A stack that has run out has no room for the handler, or for
; the throw after it, so the handler runs on a spare stack (each thread has its own:
; threads.asm's), and the throw carries on there
phi_fault_register:
    push ebx
    push esi
    push edi
    push ebp
    mov [phi_fault_to], eax
    xor ebx, ebx
    mov ecx, SPARE_STACK
    mov edx, 3                          ; read and write
    mov esi, 0x22                       ; private, not from a file
    mov edi, -1
    xor ebp, ebp
    mov eax, SYS_MMAP2
    int 0x80
    cmp eax, -4096
    ja .no_spare
    lea ecx, [eax + SPARE_STACK]
    mov [phi_main_thread + THREAD_SPARE], ecx
    call phi_spare_stack
.no_spare:
    pop ebp
    pop edi
    pop esi
    mov ebx, SIGFPE
    call .handle
    mov ebx, SIGSEGV
    call .handle
    pop ebx
    ret
.handle:
    mov eax, SYS_RT_SIGACTION
    mov ecx, phi_fault_action
    xor edx, edx                        ; (the old one isn't wanted)
    push esi
    mov esi, 8                          ; the size of a signal mask
    int 0x80
    pop esi
    ret

; eax = the bottom of SPARE_STACK bytes: where this thread's signal handler runs
phi_spare_stack:
    push ebx
    sub esp, 12                         ; Linux's stack_t: where, flags, how big
    mov [esp], eax
    mov dword [esp + 4], 0
    mov dword [esp + 8], SPARE_STACK
    mov ebx, esp
    xor ecx, ecx                        ; (the old one isn't wanted)
    mov eax, SYS_SIGALTSTACK
    int 0x80
    add esp, 12
    pop ebx
    ret

; after a throw of the stack running out, on its way back to the catch: nothing to do here,
; as what's past the end of the stack stays there to be stopped by
phi_stack_restore:
    ret

; the signal handler: [esp + 4] = the signal, [esp + 8] its siginfo (the address at +12),
; [esp + 12] the ucontext (the registers from +20 on)
phi_fault_handler:
    mov ecx, [esp + 12]
    mov eax, ERROR_DIVIDE
    cmp dword [esp + 4], SIGFPE
    je .send
    ; the stack has run out if the memory is just by the stack pointer. On the spare stack
    ; itself there's nowhere further to go.
    mov edx, [esp + 8]
    mov edx, [edx + 12]
    mov ebx, [ecx + UCONTEXT_ESP]
    lea esi, [ebx + 4096]
    cmp edx, esi
    ja .memory
    lea esi, [ebx - 65536]
    cmp edx, esi
    jb .memory
    call phi_thread_block
    mov eax, [eax + THREAD_SPARE]
    test eax, eax
    jz .again
    lea esi, [eax - SPARE_STACK]
    cmp ebx, esi
    jb .to_spare
    cmp ebx, eax
    jbe .again
.to_spare:
    mov [ecx + UCONTEXT_ESP], eax
    mov eax, ERROR_STACK
    jmp .send
.memory:
    mov eax, ERROR_ACCESS
    mov edx, [esp + 8]
    cmp dword [edx + 12], 4096
    jae .send
    mov eax, ERROR_EMPTY
.send:
    mov edx, [phi_fault_to]
    cmp [ecx + UCONTEXT_EIP], edx
    je .again                           ; the entry itself was stopped: it would only be again
    mov [ecx + UCONTEXT_EBX], eax
    mov eax, [ecx + UCONTEXT_EIP]
    mov [ecx + UCONTEXT_ECX], eax       ; where it happened, for its line
    mov [ecx + UCONTEXT_EIP], edx
    ret
.again:
    mov eax, SYS_EXIT_GROUP
    mov ebx, -1
    int 0x80

; where a signal handler returns to: the registers back from the context, as it changed them
phi_fault_return:
    mov eax, SYS_RT_SIGRETURN
    int 0x80

SYS_RT_SIGACTION equ 174
SYS_RT_SIGRETURN equ 173
SYS_MMAP2        equ 192
SYS_SIGALTSTACK  equ 186
SPARE_STACK      equ 16384
THREAD_SPARE     equ 1020               ; throw.asm's PHI_THREAD_SPARE
SIGFPE           equ 8
SIGSEGV          equ 11
SA_SIGINFO       equ 4
SA_RESTORER      equ 0x04000000
SA_ONSTACK       equ 0x08000000
ERROR_DIVIDE     equ 3                  ; ErrorKind (lib/phi/lang/error.phi)
ERROR_EMPTY      equ 4
ERROR_ACCESS     equ 5
ERROR_STACK      equ 8
UCONTEXT_ESP     equ 20 + 28
UCONTEXT_EBX     equ 20 + 32            ; in a ucontext: its sigcontext's ebx and eip
UCONTEXT_ECX     equ 20 + 40
UCONTEXT_EIP     equ 20 + 56
phi_fault_to: dd 0
phi_fault_action:                       ; the kernel's sigaction: handler, flags, restorer, mask
    dd phi_fault_handler, SA_SIGINFO | SA_RESTORER | SA_ONSTACK, phi_fault_return, 0, 0
