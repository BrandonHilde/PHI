; provides: phi_exit phi_fault_register phi_read_byte OS_ReadKey Bootloader_WaitForKeyPress phi_read_char OS_KeyAvailable OS_Sleep OS_GetTicks OS_Yield OS_CurrentTask OS_Arguments OS_ReadFile OS_ReadFileAt OS_WriteFile OS_AppendFile OS_DeleteFile OS_FileSize OS_MakeFolder OS_DeleteFolder OS_Rename OS_Grow OS_FreeMemory OS_TotalMemory OS_ClearScreen
; requires: phi_flush phi_write_all
; init: phi_linux_start
;
; What a PHI OS program asks its kernel for, answered by Linux system calls (int 0x80,
; the 32-bit ones: a number in eax, arguments in ebx, ecx, edx, esi, edi; every register
; but eax is kept). The calls PHI OS has that Linux has no answer for (the screen's
; cells, other PHI OS programs, sound, windows, the network for now) aren't here, so
; using one is an error when the program is built.
SYS_EXIT_GROUP equ 252
SYS_READ      equ 3
SYS_OPEN      equ 5
SYS_CLOSE     equ 6
SYS_UNLINK    equ 10
SYS_GETPID    equ 20
SYS_LSEEK     equ 19
SYS_RENAME    equ 38
SYS_MKDIR     equ 39
SYS_RMDIR     equ 40
SYS_BRK       equ 45
SYS_SYSINFO   equ 116
SYS_SCHED_YIELD equ 158
SYS_NANOSLEEP equ 162
SYS_POLL      equ 168
SYS_STAT64    equ 195
SYS_CLOCK_GETTIME equ 265

O_WRONLY      equ 1
O_CREAT       equ 0x40
O_TRUNC       equ 0x200
O_APPEND      equ 0x400
CLOCK_MONOTONIC equ 1
ARGUMENTS_SIZE equ 1024

; Called before the program's code, while the stack is as Linux left it: [esp + 4] is how
; many arguments there are, then their addresses, the program's own name first. Keeps the
; time it started (OS.GetTicks counts from there) and its arguments joined by spaces.
phi_linux_start:
    call phi_now
    mov [phi_started], eax
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
    mov dword [phi_poll], 0             ; standard input
    mov dword [phi_poll + 4], 1         ; anything to read?
    mov eax, SYS_POLL
    mov ebx, phi_poll
    mov ecx, 1
    xor edx, edx                        ; don't wait
    int 0x80
    cmp eax, 1
    sete al
    movzx eax, al
    ret

; eax = milliseconds since some moment (the clock that only goes forward)
phi_now:
    mov eax, SYS_CLOCK_GETTIME
    mov ebx, CLOCK_MONOTONIC
    mov ecx, phi_time
    int 0x80
    mov eax, [phi_time + 4]             ; nanoseconds
    xor edx, edx
    mov ecx, 1000000
    div ecx
    imul ecx, [phi_time], 1000          ; seconds
    add eax, ecx
    ret

; -> milliseconds since the program started
OS_GetTicks:
    call phi_now
    sub eax, [phi_started]
    ret

; arg: milliseconds
OS_Sleep:
    mov eax, [esp + 4]
    test eax, eax
    jle .done
    xor edx, edx
    mov ecx, 1000
    div ecx
    mov [phi_time], eax
    imul edx, edx, 1000000
    mov [phi_time + 4], edx
    mov eax, SYS_NANOSLEEP
    mov ebx, phi_time
    xor ecx, ecx
    int 0x80
.done:
    ret

OS_Yield:
    mov eax, SYS_SCHED_YIELD
    int 0x80
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

; ebx = path, esi = offset, edi = buffer, edx = most bytes -> eax = bytes read, or -1
phi_file_read:
    mov eax, SYS_OPEN
    xor ecx, ecx                        ; to read
    push edx
    int 0x80
    pop edx
    test eax, eax
    js .failed
    mov ebx, eax                        ; the file
    test esi, esi
    jz .read
    mov eax, SYS_LSEEK
    mov ecx, esi
    push edx
    xor edx, edx                        ; from the start
    int 0x80
    pop edx
    test eax, eax
    js .close_failed
.read:
    xor esi, esi                        ; bytes read
.more:
    cmp esi, edx
    jge .close
    mov eax, SYS_READ
    lea ecx, [edi + esi]
    push edx
    sub edx, esi
    int 0x80
    pop edx
    test eax, eax
    js .close_failed
    jz .close                           ; the end of the file
    add esi, eax
    jmp .more
.close:
    mov eax, SYS_CLOSE
    int 0x80
    mov eax, esi
    ret
.close_failed:
    mov eax, SYS_CLOSE
    int 0x80
.failed:
    mov eax, -1
    ret

; args: path buffer max -> bytes read, or -1
OS_ReadFile:
    mov ebx, [esp + 4]
    xor esi, esi
    mov edi, [esp + 8]
    mov edx, [esp + 12]
    jmp phi_file_read

; args: path offset buffer max -> bytes read, or -1
OS_ReadFileAt:
    mov ebx, [esp + 4]
    mov esi, [esp + 8]
    mov edi, [esp + 12]
    mov edx, [esp + 16]
    jmp phi_file_read

; ebx = path, ecx = O_TRUNC or O_APPEND, edi = data, edx = length -> eax = 1 if written
phi_file_write:
    mov eax, SYS_OPEN
    or ecx, O_WRONLY | O_CREAT
    push edx
    mov edx, 0o644                      ; the owner can write it, everyone can read it
    int 0x80
    pop edx
    test eax, eax
    js .failed
    mov ebx, eax
    mov ecx, edi
    call phi_write_all
    push eax
    mov eax, SYS_CLOSE
    int 0x80
    pop eax
    ret
.failed:
    xor eax, eax
    ret

; args: path data length -> 1 if written
OS_WriteFile:
    mov ebx, [esp + 4]
    mov ecx, O_TRUNC
    mov edi, [esp + 8]
    mov edx, [esp + 12]
    jmp phi_file_write

; args: path data length -> 1 if added to the end (a new file if there's none)
OS_AppendFile:
    mov ebx, [esp + 4]
    mov ecx, O_APPEND
    mov edi, [esp + 8]
    mov edx, [esp + 12]
    jmp phi_file_write

; arg: path -> its size in bytes, or -1 if it isn't a file
OS_FileSize:
    mov eax, SYS_STAT64
    mov ebx, [esp + 4]
    mov ecx, phi_stat
    int 0x80
    test eax, eax
    jnz .failed
    mov eax, [phi_stat + 16]            ; its mode
    and eax, 0xF000
    cmp eax, 0x8000                     ; a regular file
    jne .failed
    mov eax, [phi_stat + 44]            ; its size
    ret
.failed:
    mov eax, -1
    ret

; eax = what a system call gave -> 1 if it worked (0), else 0
phi_succeeded:
    test eax, eax
    sete al
    movzx eax, al
    ret

; arg: path -> 1 if deleted
OS_DeleteFile:
    mov eax, SYS_UNLINK
    mov ebx, [esp + 4]
    int 0x80
    jmp phi_succeeded

; arg: path -> 1 if made
OS_MakeFolder:
    mov eax, SYS_MKDIR
    mov ebx, [esp + 4]
    mov ecx, 0o755
    int 0x80
    jmp phi_succeeded

; arg: path -> 1 if deleted (only an empty folder)
OS_DeleteFolder:
    mov eax, SYS_RMDIR
    mov ebx, [esp + 4]
    int 0x80
    jmp phi_succeeded

; args: from to -> 1 if renamed (or moved)
OS_Rename:
    mov eax, SYS_RENAME
    mov ebx, [esp + 4]
    mov ecx, [esp + 8]
    int 0x80
    jmp phi_succeeded

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
    push edx
    mov eax, SYS_SYSINFO
    mov ebx, phi_sysinfo
    int 0x80
    pop edx
    mov eax, [phi_sysinfo + edx]
    mul dword [phi_sysinfo + 52]        ; in units of mem_unit bytes
    shrd eax, edx, 10
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
phi_started: dd 0
phi_heap_end: dd 0
phi_time: dd 0, 0
phi_poll: dd 0, 0
phi_stat: times 96 db 0
phi_sysinfo: times 64 db 0
phi_arguments: times ARGUMENTS_SIZE db 0

; eax = where this program goes when the CPU stops it (dividing by zero, or memory that
; isn't there): throw.asm's phi_fault_entry, with ebx = the kind of error. Linux sends a
; signal (SIGFPE, SIGSEGV); its handler changes the stopped code's registers in the
; signal's saved context, so when the handler returns (rt_sigreturn) it carries on there,
; as PHI OS sends a program
phi_fault_register:
    push ebx
    mov [phi_fault_to], eax
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

; the signal handler: [esp + 4] = the signal, [esp + 8] its siginfo (the address at +12),
; [esp + 12] the ucontext (the registers from +20 on)
phi_fault_handler:
    mov ecx, [esp + 12]
    mov eax, ERROR_DIVIDE
    cmp dword [esp + 4], SIGFPE
    je .send
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
SIGFPE           equ 8
SIGSEGV          equ 11
SA_SIGINFO       equ 4
SA_RESTORER      equ 0x04000000
ERROR_DIVIDE     equ 3                  ; ErrorKind (lib/phi/lang/error.phi)
ERROR_EMPTY      equ 4
ERROR_ACCESS     equ 5
UCONTEXT_EBX     equ 20 + 32            ; in a ucontext: its sigcontext's ebx and eip
UCONTEXT_EIP     equ 20 + 56
phi_fault_to: dd 0
phi_fault_action:                       ; the kernel's sigaction: handler, flags, restorer, mask
    dd phi_fault_handler, SA_SIGINFO | SA_RESTORER, phi_fault_return, 0, 0
