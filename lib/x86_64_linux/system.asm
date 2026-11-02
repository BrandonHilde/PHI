; provides: phi_exit OS_Now phi_fault_register phi_read_byte OS_ReadKey Bootloader_WaitForKeyPress phi_read_char OS_KeyAvailable OS_Sleep OS_GetTicks OS_Yield OS_CurrentTask OS_Arguments OS_ReadFile OS_ReadFileAt OS_WriteFile OS_AppendFile OS_DeleteFile OS_FileSize OS_MakeFolder OS_DeleteFolder OS_Rename OS_Grow OS_FreeMemory OS_TotalMemory OS_ClearScreen
; requires: phi_flush phi_write_all
; init: phi_linux_start
;
; What a PHI OS program asks its kernel for, answered by 64-bit Linux system calls (the
; syscall instruction: a number in rax, arguments in rdi, rsi, rdx, r10, r8, r9; rcx and
; r11 change, every other register is kept), as x86_32_linux's are by the 32-bit ones.
;
; Arguments PHI pushes are 8 bytes each in 64-bit code: the first is at [rsp + 8]. Each
; call keeps rsi and rdi.
SYS_READ       equ 0
SYS_OPEN       equ 2
SYS_CLOSE      equ 3
SYS_STAT       equ 4
SYS_POLL       equ 7
SYS_LSEEK      equ 8
SYS_BRK        equ 12
SYS_SCHED_YIELD equ 24
SYS_NANOSLEEP  equ 35
SYS_GETPID     equ 39
SYS_RENAME     equ 82
SYS_MKDIR      equ 83
SYS_RMDIR      equ 84
SYS_UNLINK     equ 87
SYS_SYSINFO    equ 99
SYS_CLOCK_GETTIME equ 228
SYS_EXIT_GROUP equ 231

O_WRONLY       equ 1
O_CREAT        equ 0x40
O_TRUNC        equ 0x200
O_APPEND       equ 0x400
CLOCK_MONOTONIC equ 1
ARGUMENTS_SIZE equ 1024

; Called before the program's code, while the stack is as Linux left it: [rsp + 8] is how
; many arguments there are, then their addresses, the program's own name first. Keeps the
; time it started (OS.GetTicks counts from there) and its arguments joined by spaces.
phi_linux_start:
    call phi_now
    mov [rel phi_started], eax
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
    mov dword [rel phi_poll], 0         ; standard input
    mov dword [rel phi_poll + 4], 1     ; anything to read?
    mov eax, SYS_POLL
    lea rdi, [rel phi_poll]
    mov esi, 1
    xor edx, edx                        ; don't wait
    syscall
    pop rdi
    pop rsi
    cmp rax, 1
    sete al
    movzx eax, al
    ret

; eax = milliseconds by the monotonic clock (only the low 32 bits: it wraps, as ticks do)
phi_now:
    push rdi
    push rsi
    mov eax, SYS_CLOCK_GETTIME
    mov edi, CLOCK_MONOTONIC
    lea rsi, [rel phi_time]
    syscall
    mov rax, [rel phi_time + 8]         ; nanoseconds
    xor edx, edx
    mov ecx, 1000000
    div rcx
    imul rcx, [rel phi_time], 1000      ; seconds
    add rax, rcx
    pop rsi
    pop rdi
    ret

; -> milliseconds since the program started
OS_GetTicks:
    call phi_now
    sub eax, [rel phi_started]
    ret

; arg: milliseconds
OS_Sleep:
    mov eax, [rsp + 8]
    test eax, eax
    jle .done
    push rdi
    push rsi
    xor edx, edx
    mov ecx, 1000
    div ecx
    mov [rel phi_time], rax
    imul edx, edx, 1000000
    mov [rel phi_time + 8], rdx
    mov eax, SYS_NANOSLEEP
    lea rdi, [rel phi_time]
    xor esi, esi
    syscall
    pop rsi
    pop rdi
.done:
    ret

OS_Yield:
    mov eax, SYS_SCHED_YIELD
    syscall
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

; rdi = path, rsi = offset, rbx = buffer, edx = most bytes -> eax = bytes read, or -1
; (changes rbx, rcx, rdx, rsi, rdi, r8, r11)
phi_file_read:
    mov r8, rsi                         ; the offset
    push rdx
    mov eax, SYS_OPEN
    xor esi, esi                        ; to read
    syscall
    pop rdx
    test rax, rax
    js .failed
    mov rdi, rax                        ; the file
    test r8, r8
    jz .read
    push rdx
    mov eax, SYS_LSEEK
    mov rsi, r8
    xor edx, edx                        ; from the start
    syscall
    pop rdx
    test rax, rax
    js .close_failed
.read:
    xor r8d, r8d                        ; bytes read
.more:
    cmp r8d, edx
    jge .close
    push rdx
    mov eax, SYS_READ
    lea rsi, [rbx + r8]
    sub edx, r8d
    syscall
    pop rdx
    test rax, rax
    js .close_failed
    jz .close                           ; the end of the file
    add r8d, eax
    jmp .more
.close:
    mov eax, SYS_CLOSE
    syscall
    mov eax, r8d
    ret
.close_failed:
    mov eax, SYS_CLOSE
    syscall
.failed:
    mov eax, -1
    ret

; args: path buffer max -> bytes read, or -1
OS_ReadFile:
    push rsi
    push rdi
    mov rdi, [rsp + 24]
    xor esi, esi
    mov rbx, [rsp + 32]
    mov edx, [rsp + 40]
    call phi_file_read
    pop rdi
    pop rsi
    ret

; args: path offset buffer max -> bytes read, or -1
OS_ReadFileAt:
    push rsi
    push rdi
    mov rdi, [rsp + 24]
    mov esi, [rsp + 32]
    mov rbx, [rsp + 40]
    mov edx, [rsp + 48]
    call phi_file_read
    pop rdi
    pop rsi
    ret

; rdi = path, esi = O_TRUNC or O_APPEND, rbx = data, r8d = length -> eax = 1 if written
; (changes rbx, rcx, rdx, rsi, rdi, r11)
phi_file_write:
    mov eax, SYS_OPEN
    or esi, O_WRONLY | O_CREAT
    mov edx, 0o644                      ; the owner can write it, everyone can read it
    syscall
    test rax, rax
    js .failed
    mov rdi, rax
    mov rsi, rbx
    mov edx, r8d
    call phi_write_all
    push rax
    mov eax, SYS_CLOSE
    syscall
    pop rax
    ret
.failed:
    xor eax, eax
    ret

; args: path data length -> 1 if written
OS_WriteFile:
    push rsi
    push rdi
    mov esi, O_TRUNC
    jmp phi_file_write_args

; args: path data length -> 1 if added to the end (a new file if there's none)
OS_AppendFile:
    push rsi
    push rdi
    mov esi, O_APPEND

; esi = how the file opens, with rsi and rdi pushed: the arguments are past them
phi_file_write_args:
    mov rdi, [rsp + 24]
    mov rbx, [rsp + 32]
    mov r8d, [rsp + 40]
    call phi_file_write
    pop rdi
    pop rsi
    ret

; arg: path -> its size in bytes, or -1 if it isn't a file
OS_FileSize:
    push rsi
    push rdi
    mov eax, SYS_STAT
    mov rdi, [rsp + 24]
    lea rsi, [rel phi_stat]
    syscall
    pop rdi
    pop rsi
    test rax, rax
    jnz .failed
    mov eax, [rel phi_stat + 24]        ; its mode
    and eax, 0xF000
    cmp eax, 0x8000                     ; a regular file
    jne .failed
    mov eax, [rel phi_stat + 48]        ; its size
    ret
.failed:
    mov eax, -1
    ret

; rax = what a system call gave -> 1 if it worked (0), else 0
phi_succeeded:
    pop rdi
    pop rsi
    test rax, rax
    sete al
    movzx eax, al
    ret

; arg: path -> 1 if deleted
OS_DeleteFile:
    push rsi
    push rdi
    mov eax, SYS_UNLINK
    mov rdi, [rsp + 24]
    syscall
    jmp phi_succeeded

; arg: path -> 1 if made
OS_MakeFolder:
    push rsi
    push rdi
    mov eax, SYS_MKDIR
    mov rdi, [rsp + 24]
    mov esi, 0o755
    syscall
    jmp phi_succeeded

; arg: path -> 1 if deleted (only an empty folder)
OS_DeleteFolder:
    push rsi
    push rdi
    mov eax, SYS_RMDIR
    mov rdi, [rsp + 24]
    syscall
    jmp phi_succeeded

; args: from to -> 1 if renamed (or moved)
OS_Rename:
    push rsi
    push rdi
    mov eax, SYS_RENAME
    mov rdi, [rsp + 24]
    mov rsi, [rsp + 32]
    syscall
    jmp phi_succeeded

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
    mov eax, SYS_SYSINFO
    lea rdi, [rel phi_sysinfo]
    syscall
    pop rdx
    pop rdi
    lea rcx, [rel phi_sysinfo]
    mov rax, [rcx + rdx]
    mov ecx, [rcx + 104]                ; in units of mem_unit bytes
    mul rcx
    shr rax, 10                         ; (a u32 of KB: up to 4 TB)
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
phi_started: dd 0
phi_poll: dd 0, 0
align 8
phi_heap_end: dq 0                      ; the break: where the heap ends
phi_time:    dq 0, 0                    ; seconds, nanoseconds
phi_stat: times 144 db 0
phi_sysinfo: times 128 db 0
phi_arguments: times ARGUMENTS_SIZE db 0

; rax = where this program goes when the CPU stops it (dividing by zero, or memory that
; isn't there): throw.asm's phi_fault_entry, with ebx = the kind of error. Linux sends a
; signal (SIGFPE, SIGSEGV); its handler changes the stopped code's registers in the
; signal's saved context, so when the handler returns (rt_sigreturn) it carries on there,
; as PHI OS sends a program
phi_fault_register:
    push rdi
    push rsi
    mov [rel phi_fault_to], rax
    mov edi, SIGFPE
    call .handle
    mov edi, SIGSEGV
    call .handle
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

; the signal handler: edi = the signal, rsi its siginfo (the address at +16), rdx the
; ucontext (the registers from +40 on)
phi_fault_handler:
    mov eax, ERROR_DIVIDE
    cmp edi, SIGFPE
    je .send
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
SIGFPE           equ 8
SIGSEGV          equ 11
SA_SIGINFO       equ 4
SA_RESTORER      equ 0x04000000
ERROR_DIVIDE     equ 3                  ; ErrorKind (lib/phi/lang/error.phi)
ERROR_EMPTY      equ 4
ERROR_ACCESS     equ 5
UCONTEXT_RBX     equ 40 + 11 * 8        ; in a ucontext: its registers' rbx and rip
UCONTEXT_RCX     equ 40 + 14 * 8
UCONTEXT_RIP     equ 40 + 16 * 8
align 8
phi_fault_to: dq 0
phi_fault_action:                       ; the kernel's sigaction: handler, flags, restorer, mask
    dq phi_fault_handler, SA_SIGINFO | SA_RESTORER, phi_fault_return, 0

; eax = the date and time, as seconds since 1970 (UTC): time
OS_Now:
    push rdi
    mov eax, 201                        ; SYS_TIME
    xor edi, edi
    syscall
    pop rdi
    ret
